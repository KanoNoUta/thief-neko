import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { Duplex } from 'node:stream';
import { CredentialBroker } from '../src/credentialBroker.js';

const PIPE_NAME = 'catapi-credential-test';
const NONCE = 'nonce-secret-value';
const TOKEN = 'access-token-secret';

test('CredentialBroker requests one snapshot line and returns the exact normalized snapshot', async () => {
  const transport = fakeTransport(({ path, request }) => {
    assert.equal(path, `\\\\.\\pipe\\${PIPE_NAME}`);
    assert.deepEqual(request, { nonce: NONCE, operation: 'snapshot' });
    return okSnapshot('token-1', 7);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  assert.deepEqual(await broker.snapshot(), {
    token: 'token-1',
    userMis: 'user-1',
    cookie: 'passport=token-1',
    generation: 7,
  });
  assert.equal(transport.sockets[0].written.endsWith('\n'), true);
  assert.equal(transport.sockets[0].destroyCalls, 1);
});

test('CredentialBroker snapshot requests fresh state on every call', async () => {
  let requests = 0;
  const transport = fakeTransport(() => {
    requests += 1;
    return okSnapshot(`token-${requests}`, requests);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  assert.equal((await broker.snapshot()).token, 'token-1');
  assert.equal((await broker.snapshot()).token, 'token-2');
  assert.equal(requests, 2);
  assert.equal(transport.sockets.length, 2);
});

test('CredentialBroker ignores an older snapshot that arrives after a newer generation', async () => {
  const releases = [];
  const transport = fakeTransport(() => new Promise((resolve) => releases.push(resolve)));
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const older = broker.snapshot();
  const newer = broker.snapshot();
  await waitFor(() => releases.length === 2);
  releases[1](okSnapshot('newer-token', 2));
  assert.equal((await newer).token, 'newer-token');
  releases[0](okSnapshot('older-token', 1));

  assert.deepEqual(await older, {
    token: 'newer-token',
    userMis: 'user-1',
    cookie: 'passport=newer-token',
    generation: 2,
  });
});

test('CredentialBroker rejects conflicting credentials at the same generation', async () => {
  let request = 0;
  const transport = fakeTransport(() => {
    request += 1;
    return okSnapshot(request === 1 ? 'first-token' : 'conflicting-token', 4);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });
  await broker.snapshot();

  const error = await captureError(() => broker.snapshot());
  assert.equal(error.code, 'CREDENTIAL_BROKER_MALFORMED');
  assertSecretFree(error, 'conflicting-token');
});

test('CredentialBroker refreshes after unauthorized with the token that was used', async () => {
  const requests = [];
  const transport = fakeTransport(({ request }) => {
    requests.push(request);
    return requests.length === 1
      ? okSnapshot(TOKEN, 3)
      : okSnapshot('rotated-token', 4);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });
  await broker.snapshot();

  assert.equal(await broker.refreshAfterUnauthorized(TOKEN), true);
  assert.deepEqual(requests[1], {
    nonce: NONCE,
    operation: 'refresh',
    usedToken: TOKEN,
  });
  assert.equal((await broker.snapshot()).token, 'rotated-token');
});

test('CredentialBroker coalesces refreshes and skips them after the current token changes', async () => {
  let releaseRefresh;
  let refreshRequests = 0;
  const pending = new Promise((resolve) => { releaseRefresh = resolve; });
  const transport = fakeTransport(async ({ request }) => {
    if (request.operation === 'snapshot') return okSnapshot(TOKEN, 1);
    refreshRequests += 1;
    await pending;
    return okSnapshot('new-token', 2);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });
  await broker.snapshot();

  const first = broker.refreshAfterUnauthorized(TOKEN);
  const second = broker.refreshAfterUnauthorized(TOKEN);
  releaseRefresh();

  assert.deepEqual(await Promise.all([first, second]), [true, true]);
  assert.equal(await broker.refreshAfterUnauthorized(TOKEN), true);
  assert.equal(refreshRequests, 1);
});

test('CredentialBroker returns one unchanged outcome to same-token concurrent callers', async () => {
  let releaseFirst;
  let refreshRequests = 0;
  const firstResponse = new Promise((resolve) => { releaseFirst = resolve; });
  const transport = fakeTransport(({ request }) => {
    if (request.operation === 'snapshot') return okSnapshot(TOKEN, 1);
    refreshRequests += 1;
    return refreshRequests === 1
      ? firstResponse
      : okSnapshot('second-refresh-token', 2);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });
  await broker.snapshot();

  const owner = broker.refreshAfterUnauthorized(TOKEN);
  const joiner = broker.refreshAfterUnauthorized(TOKEN);
  assert.equal(joiner, owner);
  releaseFirst(okSnapshot(TOKEN, 1));

  assert.deepEqual(await Promise.all([owner, joiner]), [false, false]);
  assert.equal(refreshRequests, 1);
  assert.equal(await broker.refreshAfterUnauthorized(TOKEN), true);
  assert.equal(refreshRequests, 2);
});

test('CredentialBroker does not join a pending refresh for a different used token', async () => {
  let snapshotRequests = 0;
  let releaseTokenA;
  const refreshA = new Promise((resolve) => { releaseTokenA = resolve; });
  const requests = [];
  const transport = fakeTransport(({ request }) => {
    requests.push(request);
    if (request.operation === 'snapshot') {
      snapshotRequests += 1;
      return snapshotRequests === 1
        ? okSnapshot('token-A', 1)
        : okSnapshot(snapshotRequests === 2 ? 'token-B' : 'token-C', snapshotRequests);
    }
    if (request.usedToken === 'token-A') return refreshA;
    return okSnapshot('token-C', 3);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });
  await broker.snapshot();

  const first = broker.refreshAfterUnauthorized('token-A');
  await waitFor(() => requests.some((request) => request.usedToken === 'token-A'));
  assert.equal((await broker.snapshot()).token, 'token-B');
  const second = broker.refreshAfterUnauthorized('token-B');
  await waitFor(
    () => requests.some((request) => request.operation === 'refresh' && request.usedToken === 'token-B'),
    100,
  );
  assert.equal(await second, true);
  releaseTokenA(okSnapshot('token-B', 2));
  assert.equal(await first, true);
  assert.equal((await broker.snapshot()).token, 'token-C');
});

test('CredentialBroker destroys a timed out socket and redacts the error', async () => {
  const transport = fakeTransport(() => new Promise(() => {}));
  const broker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: NONCE,
    connect: transport.connect,
    timeoutMs: 20,
  });

  const error = await captureError(() => broker.poll());
  assert.equal(error.code, 'CREDENTIAL_BROKER_TIMEOUT');
  assert.equal(transport.sockets[0].destroyed, true);
  assertSecretFree(error);
});

test('CredentialBroker rejects malformed responses without retaining raw content', async () => {
  const raw = `not-json-${NONCE}-${TOKEN}`;
  const transport = fakeTransport(() => raw);
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const error = await captureError(() => broker.poll());
  assert.equal(error.code, 'CREDENTIAL_BROKER_MALFORMED');
  assert.equal(transport.sockets[0].destroyed, true);
  assertSecretFree(error, raw);
});

test('CredentialBroker parses a snapshot fragmented across arbitrary chunks', async () => {
  const payload = Buffer.from(JSON.stringify(okSnapshot('fragmented-token', 8)), 'utf8');
  const chunks = [
    payload.subarray(0, 1),
    payload.subarray(1, 7),
    payload.subarray(7),
    Buffer.from('\n'),
  ].map((chunk) => Buffer.from(chunk));
  const transport = fakeTransport(() => ({ rawChunks: chunks }));
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  assert.equal((await broker.snapshot()).token, 'fragmented-token');
});

test('CredentialBroker rejects a connection closed before a complete frame', async () => {
  const transport = fakeTransport(() => ({
    rawChunks: [Buffer.from('{"ok":true')],
    end: true,
  }));
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const error = await captureError(() => broker.snapshot());
  assert.equal(error.code, 'CREDENTIAL_BROKER_MALFORMED');
  assertSecretFree(error);
});

test('CredentialBroker rejects invalid UTF-8 as malformed', async () => {
  const invalid = Buffer.from([
    0x7b, 0x22, 0x6f, 0x6b, 0x22, 0x3a, 0x66, 0x61, 0x6c, 0x73, 0x65,
    0x2c, 0x22, 0x65, 0x72, 0x72, 0x6f, 0x72, 0x22, 0x3a, 0x22,
    0xc3, 0x28, 0x22, 0x7d, 0x0a,
  ]);
  const transport = fakeTransport(() => ({ rawChunks: [invalid] }));
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const error = await captureError(() => broker.snapshot());
  assert.equal(error.code, 'CREDENTIAL_BROKER_MALFORMED');
  assertSecretFree(error);
});

test('CredentialBroker redacts unauthorized server responses', async () => {
  const transport = fakeTransport(() => ({
    ok: false,
    error: 'unauthorized',
  }));
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const error = await captureError(() => broker.poll());
  assert.equal(error.code, 'CREDENTIAL_BROKER_UNAUTHORIZED');
  assertSecretFree(error);
});

test('CredentialBroker maps stable server error strings without exposing raw content', async () => {
  const cases = [
    ['malformed', 'CREDENTIAL_BROKER_MALFORMED'],
    ['oversize', 'CREDENTIAL_BROKER_OVERSIZE'],
    ['unknown', 'CREDENTIAL_BROKER_REJECTED'],
  ];

  for (const [serverError, expectedCode] of cases) {
    const transport = fakeTransport(() => ({ ok: false, error: serverError }));
    const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });
    const error = await captureError(() => broker.snapshot());
    assert.equal(error.code, expectedCode);
    assertSecretFree(error, JSON.stringify({ ok: false, error: serverError }));
  }
});

test('CredentialBroker rejects oversized responses and destroys the socket', async () => {
  const transport = fakeTransport(() => `{"padding":"${'x'.repeat(16 * 1024)}"}`);
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const error = await captureError(() => broker.poll());
  assert.equal(error.code, 'CREDENTIAL_BROKER_OVERSIZE');
  assert.equal(transport.sockets[0].destroyed, true);
  assertSecretFree(error);
});

test('CredentialBroker counts inbound payload bytes excluding the newline', async () => {
  const exactPayload = paddedErrorPayload(16 * 1024);
  const exactTransport = fakeTransport(() => exactPayload);
  const exactBroker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: NONCE,
    connect: exactTransport.connect,
  });
  const exactError = await captureError(() => exactBroker.snapshot());
  assert.equal(exactError.code, 'CREDENTIAL_BROKER_REJECTED');

  const oversizedPayload = paddedErrorPayload((16 * 1024) + 1);
  const oversizedTransport = fakeTransport(() => oversizedPayload);
  const oversizedBroker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: NONCE,
    connect: oversizedTransport.connect,
  });
  const oversizedError = await captureError(() => oversizedBroker.snapshot());
  assert.equal(oversizedError.code, 'CREDENTIAL_BROKER_OVERSIZE');
});

test('CredentialBroker rejects oversized outbound frames before connecting', async () => {
  const oversizedNonce = `outbound-secret-${'x'.repeat(16 * 1024)}`;
  let connectCalls = 0;
  const broker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: oversizedNonce,
    connect: () => {
      connectCalls += 1;
      throw new Error('connector must not run');
    },
  });

  const error = await captureError(() => broker.snapshot());
  assert.equal(error.code, 'CREDENTIAL_BROKER_OVERSIZE');
  assert.equal(connectCalls, 0);
  assert.equal(`${error.message} ${error.stack}`.includes(oversizedNonce), false);
});

test('CredentialBroker counts outbound payload bytes excluding the newline', async () => {
  const baseBytes = Buffer.byteLength(JSON.stringify({ nonce: '', operation: 'snapshot' }));
  const exactNonce = 'n'.repeat((16 * 1024) - baseBytes);
  const exactTransport = fakeTransport(() => okSnapshot('exact-token', 1));
  const exactBroker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: exactNonce,
    connect: exactTransport.connect,
  });
  assert.equal((await exactBroker.snapshot()).token, 'exact-token');
  assert.equal(exactTransport.sockets.length, 1);

  let oversizedConnects = 0;
  const oversizedBroker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: `${exactNonce}n`,
    connect: () => {
      oversizedConnects += 1;
      throw new Error('connector must not run');
    },
  });
  const error = await captureError(() => oversizedBroker.snapshot());
  assert.equal(error.code, 'CREDENTIAL_BROKER_OVERSIZE');
  assert.equal(oversizedConnects, 0);
});

test('CredentialBroker zeroes outbound bytes only after the async write callback', async () => {
  const socket = new ManualWriteSocket(okSnapshot('write-token', 1));
  const broker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: NONCE,
    connect: () => socket,
  });

  assert.equal((await broker.snapshot()).token, 'write-token');
  assert.equal(socket.frame.some((byte) => byte !== 0), true);
  socket.completeWrite();
  assert.equal(socket.frame.every((byte) => byte === 0), true);
});

test('CredentialBroker zeroes outbound bytes when a destroyed socket closes', async () => {
  const socket = new ManualWriteSocket(okSnapshot('close-token', 1), true);
  const broker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: NONCE,
    connect: () => socket,
  });

  assert.equal((await broker.snapshot()).token, 'close-token');
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(socket.frame.every((byte) => byte === 0), true);
});

test('CredentialBroker zeroes inbound source buffers after parsing', async () => {
  const response = Buffer.from(`${JSON.stringify(okSnapshot('zeroed-token', 1))}\n`, 'utf8');
  const transport = fakeTransport(() => ({ rawChunks: [response] }));
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  assert.equal((await broker.snapshot()).token, 'zeroed-token');
  assert.equal(response.every((byte) => byte === 0), true);
});

test('CredentialBroker start and stop own one unrefed polling timer', () => {
  let callback;
  let cleared;
  let unrefed = false;
  const timer = { unref: () => { unrefed = true; } };
  const broker = new CredentialBroker({
    pipeName: PIPE_NAME,
    nonce: NONCE,
    connect: () => { throw new Error('unused'); },
    setIntervalFn: (fn, milliseconds) => {
      callback = fn;
      assert.equal(milliseconds, 5_000);
      return timer;
    },
    clearIntervalFn: (value) => { cleared = value; },
  });

  broker.start();
  broker.start();
  assert.equal(typeof callback, 'function');
  assert.equal(unrefed, true);
  broker.stop();
  assert.equal(cleared, timer);
});

function okSnapshot(token, generation) {
  return {
    ok: true,
    snapshot: {
      token,
      userMis: 'user-1',
      cookie: `passport=${token}`,
      generation,
      refreshToken: 'must-not-be-retained',
    },
  };
}

function fakeTransport(respond) {
  const sockets = [];
  return {
    sockets,
    connect(path) {
      const socket = new FakeSocket(path, respond);
      sockets.push(socket);
      return socket;
    },
  };
}

class FakeSocket extends Duplex {
  constructor(path, respond) {
    super();
    this.path = path;
    this.respond = respond;
    this.written = '';
    this.destroyCalls = 0;
    queueMicrotask(() => this.emit('connect'));
  }

  _read() {}

  _write(chunk, encoding, callback) {
    this.written += chunk.toString('utf8');
    callback();
    if (!this.written.includes('\n')) return;

    const request = JSON.parse(this.written.slice(0, this.written.indexOf('\n')));
    Promise.resolve(this.respond({ path: this.path, request, socket: this }))
      .then((response) => {
        if (this.destroyed) return;
        if (response?.rawChunks) {
          for (const chunk of response.rawChunks) this.push(chunk);
          if (response.end) this.push(null);
          return;
        }
        const raw = typeof response === 'string' ? response : JSON.stringify(response);
        this.push(`${raw}\n`);
      })
      .catch((error) => this.destroy(error));
  }

  _destroy(error, callback) {
    this.destroyCalls += 1;
    callback(error);
  }
}

class ManualWriteSocket extends EventEmitter {
  constructor(response, emitCloseOnDestroy = false) {
    super();
    this.response = response;
    this.emitCloseOnDestroy = emitCloseOnDestroy;
    this.destroyed = false;
    queueMicrotask(() => this.emit('connect'));
  }

  write(frame, callback) {
    this.frame = frame;
    this.writeCallback = callback;
    queueMicrotask(() => this.emit('data', Buffer.from(`${JSON.stringify(this.response)}\n`)));
    return true;
  }

  completeWrite() {
    this.writeCallback();
  }

  destroy() {
    this.destroyed = true;
    if (this.emitCloseOnDestroy) queueMicrotask(() => this.emit('close'));
  }
}

function paddedErrorPayload(length) {
  const prefix = '{"ok":false,"error":"unknown","padding":"';
  const suffix = '"}';
  return `${prefix}${'x'.repeat(length - Buffer.byteLength(prefix) - Buffer.byteLength(suffix))}${suffix}`;
}

function assertSecretFree(error, raw = '') {
  const text = `${error.name} ${error.message} ${error.stack || ''}`;
  for (const secret of [NONCE, TOKEN, raw]) {
    if (secret) assert.equal(text.includes(secret), false);
  }
}

async function captureError(action) {
  try {
    await action();
  } catch (error) {
    return error;
  }
  assert.fail('expected action to reject');
}

async function waitFor(predicate, timeoutMs = 1_000) {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() >= deadline) assert.fail('condition was not met before timeout');
    await new Promise((resolve) => setTimeout(resolve, 1));
  }
}
