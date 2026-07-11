import test from 'node:test';
import assert from 'node:assert/strict';
import { Duplex } from 'node:stream';
import { CredentialBroker } from '../src/credentialBroker.js';

const PIPE_NAME = 'catapi-credential-test';
const NONCE = 'nonce-secret-value';
const TOKEN = 'access-token-secret';

test('CredentialBroker polls one snapshot line and exposes the exact normalized snapshot', async () => {
  const transport = fakeTransport(({ path, request }) => {
    assert.equal(path, `\\\\.\\pipe\\${PIPE_NAME}`);
    assert.deepEqual(request, { nonce: NONCE, operation: 'snapshot' });
    return okSnapshot('token-1', 7);
  });
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  assert.equal(await broker.poll(), true);
  assert.deepEqual(broker.snapshot(), {
    token: 'token-1',
    userMis: 'user-1',
    cookie: 'passport=token-1',
    generation: 7,
  });
  assert.equal(transport.sockets[0].written.endsWith('\n'), true);
  assert.equal(transport.sockets[0].writableEnded, true);
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
  await broker.poll();

  assert.equal(await broker.refreshAfterUnauthorized(TOKEN), true);
  assert.deepEqual(requests[1], {
    nonce: NONCE,
    operation: 'refresh',
    usedToken: TOKEN,
  });
  assert.equal(broker.snapshot().token, 'rotated-token');
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
  await broker.poll();

  const first = broker.refreshAfterUnauthorized(TOKEN);
  const second = broker.refreshAfterUnauthorized(TOKEN);
  releaseRefresh();

  assert.deepEqual(await Promise.all([first, second]), [true, true]);
  assert.equal(await broker.refreshAfterUnauthorized(TOKEN), true);
  assert.equal(refreshRequests, 1);
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

test('CredentialBroker redacts unauthorized server responses', async () => {
  const transport = fakeTransport(() => ({
    ok: false,
    error: { code: 'unauthorized', message: `denied ${NONCE} ${TOKEN}` },
  }));
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const error = await captureError(() => broker.poll());
  assert.equal(error.code, 'CREDENTIAL_BROKER_UNAUTHORIZED');
  assertSecretFree(error);
});

test('CredentialBroker rejects oversized responses and destroys the socket', async () => {
  const transport = fakeTransport(() => `{"padding":"${'x'.repeat(16 * 1024)}"}`);
  const broker = new CredentialBroker({ pipeName: PIPE_NAME, nonce: NONCE, connect: transport.connect });

  const error = await captureError(() => broker.poll());
  assert.equal(error.code, 'CREDENTIAL_BROKER_OVERSIZE');
  assert.equal(transport.sockets[0].destroyed, true);
  assertSecretFree(error);
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
        const raw = typeof response === 'string' ? response : JSON.stringify(response);
        this.push(`${raw}\n`);
      })
      .catch((error) => this.destroy(error));
  }
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
