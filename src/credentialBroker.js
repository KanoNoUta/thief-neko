import net from 'node:net';

const MAX_MESSAGE_BYTES = 16 * 1024;
const DEFAULT_TIMEOUT_MS = 2_000;
const DEFAULT_POLL_INTERVAL_MS = 5_000;

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
    this.state = { token: '', userMis: '', cookie: '', generation: 0 };
    this.refreshPromise = null;
    this.timer = null;
  }

  snapshot() {
    return { ...this.state };
  }

  async poll() {
    const before = this.state;
    const snapshot = await this.#request({ operation: 'snapshot' });
    this.#apply(snapshot);
    return !snapshotsEqual(before, this.state);
  }

  async refreshAfterUnauthorized(usedToken) {
    if (this.state.token !== usedToken) {
      return true;
    }
    if (this.refreshPromise) {
      return this.refreshPromise;
    }

    const refresh = this.#refresh(usedToken);
    this.refreshPromise = refresh;
    try {
      return await refresh;
    } finally {
      if (this.refreshPromise === refresh) {
        this.refreshPromise = null;
      }
    }
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
    return this.state.token !== usedToken;
  }

  #apply(snapshot) {
    if (snapshot.generation < this.state.generation) {
      throw brokerError('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
    }
    if (snapshot.generation === this.state.generation
      && this.state.token
      && !snapshotsEqual(snapshot, this.state)) {
      throw brokerError('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
    }
    this.state = snapshot;
  }

  #request(payload) {
    return new Promise((resolve, reject) => {
      let socket;
      let settled = false;
      let sent = false;
      let receivedBytes = 0;
      const chunks = [];

      const finish = (error, value, destroy) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        socket?.removeListener('connect', send);
        socket?.removeListener('data', onData);
        socket?.removeListener('error', onError);
        socket?.removeListener('end', onEnd);
        if (destroy) socket?.destroy();
        else socket?.end();
        if (error) reject(error);
        else resolve(value);
      };

      const fail = (code, label) => finish(brokerError(code, label), undefined, true);
      const send = () => {
        if (sent || settled) return;
        sent = true;
        try {
          socket.write(`${JSON.stringify({ nonce: this.nonce, ...payload })}\n`);
        } catch {
          fail('CREDENTIAL_BROKER_TRANSPORT', 'transport failed');
        }
      };
      const onData = (chunk) => {
        const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
        receivedBytes += bytes.length;
        if (receivedBytes > MAX_MESSAGE_BYTES) {
          fail('CREDENTIAL_BROKER_OVERSIZE', 'response was oversized');
          return;
        }
        chunks.push(bytes);
        const combined = Buffer.concat(chunks, receivedBytes);
        const newline = combined.indexOf(0x0a);
        if (newline < 0) return;

        let response;
        try {
          response = JSON.parse(combined.subarray(0, newline).toString('utf8'));
        } catch {
          fail('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
          return;
        }
        if (!response || typeof response !== 'object' || Array.isArray(response)) {
          fail('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
          return;
        }
        if (response.ok !== true) {
          const unauthorized = response?.error?.code === 'unauthorized';
          fail(
            unauthorized ? 'CREDENTIAL_BROKER_UNAUTHORIZED' : 'CREDENTIAL_BROKER_REJECTED',
            unauthorized ? 'request was unauthorized' : 'request was rejected',
          );
          return;
        }

        const snapshot = normalizeSnapshot(response.snapshot);
        if (!snapshot) {
          fail('CREDENTIAL_BROKER_MALFORMED', 'response was malformed');
          return;
        }
        finish(null, snapshot, false);
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

function brokerError(code, label) {
  const error = new Error(`Credential broker ${label}.`);
  error.name = 'CredentialBrokerError';
  error.code = code;
  return error;
}
