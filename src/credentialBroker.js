import net from 'node:net';

const MAX_MESSAGE_BYTES = 16 * 1024;
const DEFAULT_TIMEOUT_MS = 2_000;
const DEFAULT_POLL_INTERVAL_MS = 5_000;
const STRICT_UTF8_DECODER = new TextDecoder('utf-8', { fatal: true });

export class CredentialBroker {
  constructor({
    pipeName,
    nonce,
    connect = (path) => net.createConnection(path),
    timeoutMs = DEFAULT_TIMEOUT_MS,
    pollIntervalMs = DEFAULT_POLL_INTERVAL_MS,
    setIntervalFn = setInterval,
    clearIntervalFn = clearInterval,
  }) {
    if (typeof pipeName !== 'string' || !pipeName.trim()) {
      throw new TypeError('pipeName must be a non-empty string');
    }
    if (typeof nonce !== 'string' || !nonce) {
      throw new TypeError('nonce must be a non-empty string');
    }
    if (typeof connect !== 'function') {
      throw new TypeError('connect must be a function');
    }
    if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0) {
      throw new RangeError('timeoutMs must be a positive safe integer');
    }
    if (!Number.isSafeInteger(pollIntervalMs) || pollIntervalMs <= 0) {
      throw new RangeError('pollIntervalMs must be a positive safe integer');
    }

    this.pipePath = `\\\\.\\pipe\\${pipeName.trim()}`;
    this.nonce = nonce;
    this.connect = connect;
    this.timeoutMs = timeoutMs;
    this.pollIntervalMs = pollIntervalMs;
    this.setIntervalFn = setIntervalFn;
    this.clearIntervalFn = clearIntervalFn;
    this.cachedCurrent = { token: '', userMis: '', cookie: '', generation: 0 };
    this.refreshes = new Map();
    this.timer = null;
  }

  async snapshot() {
    const snapshot = await this.#request({ operation: 'snapshot' });
    return this.#apply(snapshot);
  }

  async poll() {
    const before = this.cachedCurrent;
    await this.snapshot();
    return !snapshotsEqual(before, this.cachedCurrent);
  }

  async refreshAfterUnauthorized(usedToken) {
    if (this.cachedCurrent.token !== usedToken) {
      return true;
    }
    const active = this.refreshes.get(usedToken);
    if (active) {
      await active;
      if (this.cachedCurrent.token !== usedToken) {
        return true;
      }
      return this.#getOrStartRefresh(usedToken);
    }

    return this.#getOrStartRefresh(usedToken);
  }

  start() {
    if (this.timer) return;
    this.timer = this.setIntervalFn(
      () => this.poll().catch(() => false),
      this.pollIntervalMs,
    );
    this.timer?.unref?.();
  }

  stop() {
    if (!this.timer) return;
    this.clearIntervalFn(this.timer);
    this.timer = null;
  }

  async #refresh(usedToken) {
    const snapshot = await this.#request({ operation: 'refresh', usedToken });
    this.#apply(snapshot);
    return this.cachedCurrent.token !== usedToken;
  }

  #getOrStartRefresh(usedToken) {
    const active = this.refreshes.get(usedToken);
    if (active) return active;

    let tracked;
    tracked = this.#refresh(usedToken).finally(() => {
      if (this.refreshes.get(usedToken) === tracked) {
        this.refreshes.delete(usedToken);
      }
    });
    this.refreshes.set(usedToken, tracked);
    return tracked;
  }

  #apply(snapshot) {
    if (snapshot.generation < this.cachedCurrent.generation) {
      return { ...this.cachedCurrent };
    }
    if (snapshot.generation === this.cachedCurrent.generation
      && this.cachedCurrent.token
      && !snapshotsEqual(snapshot, this.cachedCurrent)) {
      throw brokerError('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
    }
    this.cachedCurrent = snapshot;
    return { ...this.cachedCurrent };
  }

  #request(payload) {
    const encodedPayload = Buffer.from(
      JSON.stringify({ nonce: this.nonce, ...payload }),
      'utf8',
    );
    if (encodedPayload.length > MAX_MESSAGE_BYTES) {
      encodedPayload.fill(0);
      return Promise.reject(
        brokerError('CREDENTIAL_BROKER_OVERSIZE', 'request was oversized'),
      );
    }
    const frame = Buffer.allocUnsafe(encodedPayload.length + 1);
    encodedPayload.copy(frame);
    frame[frame.length - 1] = 0x0a;
    encodedPayload.fill(0);

    return new Promise((resolve, reject) => {
      let socket;
      let settled = false;
      let sent = false;
      let writeStarted = false;
      let receivedBytes = 0;
      const inbound = Buffer.allocUnsafe(MAX_MESSAGE_BYTES);
      const zeroFrame = () => frame.fill(0);

      const finish = (error, value) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        socket?.removeListener('connect', send);
        socket?.removeListener('data', onData);
        socket?.removeListener('error', onError);
        socket?.removeListener('end', onEnd);
        socket?.destroy();
        inbound.fill(0);
        if (!writeStarted) zeroFrame();
        if (error) reject(error);
        else resolve(value);
      };

      const fail = (code, label) => finish(brokerError(code, label));
      const send = () => {
        if (sent || settled) return;
        sent = true;
        try {
          writeStarted = true;
          socket.write(frame, (error) => {
            zeroFrame();
            if (error && !settled) {
              fail('CREDENTIAL_BROKER_TRANSPORT', 'transport failed');
            }
          });
        } catch {
          zeroFrame();
          fail('CREDENTIAL_BROKER_TRANSPORT', 'transport failed');
        }
      };
      const onData = (chunk) => {
        const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
        try {
          const newline = bytes.indexOf(0x0a);
          const payloadBytes = newline < 0 ? bytes.length : newline;
          if (receivedBytes + payloadBytes > MAX_MESSAGE_BYTES) {
            fail('CREDENTIAL_BROKER_OVERSIZE', 'response was oversized');
            return;
          }
          bytes.copy(inbound, receivedBytes, 0, payloadBytes);
          receivedBytes += payloadBytes;
          if (newline < 0) return;

          let response;
          try {
            response = JSON.parse(
              STRICT_UTF8_DECODER.decode(inbound.subarray(0, receivedBytes)),
            );
          } catch {
            fail('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
            return;
          }
          if (!response || typeof response !== 'object' || Array.isArray(response)) {
            fail('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
            return;
          }
          if (response.ok !== true) {
            const mapped = mapServerError(response.error);
            fail(mapped.code, mapped.label);
            return;
          }

          const snapshot = normalizeSnapshot(response.snapshot);
          if (!snapshot) {
            fail('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
            return;
          }
          finish(null, snapshot);
        } finally {
          bytes.fill(0);
        }
      };
      const onError = () => fail('CREDENTIAL_BROKER_TRANSPORT', 'transport failed');
      const onEnd = () => {
        if (!settled) fail('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
      };
      const timer = setTimeout(
        () => fail('CREDENTIAL_BROKER_TIMEOUT', 'request timed out'),
        this.timeoutMs,
      );
      timer.unref?.();

      try {
        socket = this.connect(this.pipePath);
        if (!socket || typeof socket.on !== 'function' || typeof socket.write !== 'function') {
          fail('CREDENTIAL_BROKER_TRANSPORT', 'transport failed');
          return;
        }
        socket.once('connect', send);
        socket.once('close', zeroFrame);
        socket.on('data', onData);
        socket.once('error', onError);
        socket.once('end', onEnd);
      } catch {
        fail('CREDENTIAL_BROKER_TRANSPORT', 'transport failed');
      }
    });
  }
}

function normalizeSnapshot(snapshot) {
  if (!snapshot || typeof snapshot !== 'object' || Array.isArray(snapshot)) return null;
  const { token, userMis, cookie, generation } = snapshot;
  if (typeof token !== 'string' || !token
    || typeof userMis !== 'string' || !userMis
    || typeof cookie !== 'string'
    || !Number.isSafeInteger(generation) || generation < 0) {
    return null;
  }
  return { token, userMis, cookie, generation };
}

function snapshotsEqual(left, right) {
  return left.token === right.token
    && left.userMis === right.userMis
    && left.cookie === right.cookie
    && left.generation === right.generation;
}

function mapServerError(value) {
  switch (value) {
    case 'unauthorized':
      return {
        code: 'CREDENTIAL_BROKER_UNAUTHORIZED',
        label: 'request was unauthorized',
      };
    case 'malformed':
      return {
        code: 'CREDENTIAL_BROKER_MALFORMED',
        label: 'response was malformed',
      };
    case 'oversize':
      return {
        code: 'CREDENTIAL_BROKER_OVERSIZE',
        label: 'response was oversized',
      };
    default:
      return {
        code: 'CREDENTIAL_BROKER_REJECTED',
        label: 'request was rejected',
      };
  }
}

function brokerError(code, label) {
  const error = new Error(`Credential broker ${label}.`);
  error.name = 'CredentialBrokerError';
  error.code = code;
  return error;
}
