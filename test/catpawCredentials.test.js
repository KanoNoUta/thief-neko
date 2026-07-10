import test from 'node:test';
import assert from 'node:assert/strict';
import { CatpawCredentialManager } from '../src/catpawCredentials.js';

test('CatpawCredentialManager atomically replaces token, user headers, and credential cookies', async () => {
  const manager = new CatpawCredentialManager({
    token: 'old-token',
    cookie: 'passport=old-token; sso=old-token; theme=dark',
    userMis: 'old-user',
    readSession: async () => ({ token: 'new-token', userMis: 'new-user' }),
  });

  assert.equal(await manager.poll(), true);
  assert.deepEqual(manager.snapshot(), {
    token: 'new-token',
    cookie: 'passport=new-token; sso=new-token; theme=dark',
    userMis: 'new-user',
    generation: 1,
  });
});

test('CatpawCredentialManager preserves the last credentials when state reading fails', async () => {
  const manager = new CatpawCredentialManager({
    token: 'stable-token',
    cookie: 'passport=stable-token',
    userMis: 'stable-user',
    readSession: async () => { throw new Error('database busy'); },
  });

  assert.equal(await manager.poll(), false);
  assert.deepEqual(manager.snapshot(), {
    token: 'stable-token',
    cookie: 'passport=stable-token',
    userMis: 'stable-user',
    generation: 0,
  });
});

test('CatpawCredentialManager shares one refresh across concurrent unauthorized requests', async () => {
  let resolveRead;
  let reads = 0;
  const pendingRead = new Promise((resolve) => { resolveRead = resolve; });
  const manager = new CatpawCredentialManager({
    token: 'old-token',
    cookie: 'passport=old-token',
    userMis: 'user-1',
    refreshAttempts: 1,
    readSession: async () => {
      reads += 1;
      return pendingRead;
    },
  });

  const first = manager.refreshAfterUnauthorized('old-token');
  const second = manager.refreshAfterUnauthorized('old-token');
  resolveRead({ token: 'new-token', userMis: 'user-1' });

  assert.deepEqual(await Promise.all([first, second]), [true, true]);
  assert.equal(reads, 1);
  assert.equal(manager.snapshot().token, 'new-token');
});

test('CatpawCredentialManager starts a new refresh for a rejection of the newly installed token', async () => {
  let reads = 0;
  let secondRefresh;
  let manager;
  manager = new CatpawCredentialManager({
    token: 'old-token',
    refreshAttempts: 1,
    readSession: async () => {
      reads += 1;
      return reads === 1
        ? { token: 'new-token', userMis: 'user-1' }
        : { token: 'newest-token', userMis: 'user-1' };
    },
    onRefresh: ({ generation }) => {
      if (generation === 1) {
        secondRefresh = manager.refreshAfterUnauthorized('new-token');
      }
    },
  });

  assert.equal(await manager.refreshAfterUnauthorized('old-token'), true);
  assert.equal(await secondRefresh, true);
  assert.equal(reads, 2);
  assert.equal(manager.snapshot().token, 'newest-token');
});

test('CatpawCredentialManager does not report a refresh when local state is unchanged', async () => {
  let reads = 0;
  const manager = new CatpawCredentialManager({
    token: 'same-token',
    refreshAttempts: 3,
    refreshDelayMs: 0,
    sleep: async () => {},
    readSession: async () => {
      reads += 1;
      return { token: 'same-token', userMis: 'user-1' };
    },
  });

  assert.equal(await manager.refreshAfterUnauthorized('same-token'), false);
  assert.equal(reads, 3);
});

test('CatpawCredentialManager starts an unrefed poll timer and clears it on stop', async () => {
  let scheduled;
  let interval;
  let cleared;
  let unrefed = false;
  const timer = { unref: () => { unrefed = true; } };
  const manager = new CatpawCredentialManager({
    token: 'old-token',
    pollIntervalMs: 5_000,
    readSession: async () => ({ token: 'new-token', userMis: 'user-1' }),
    setIntervalFn: (callback, milliseconds) => {
      scheduled = callback;
      interval = milliseconds;
      return timer;
    },
    clearIntervalFn: (value) => { cleared = value; },
  });

  manager.start();
  assert.equal(interval, 5_000);
  assert.equal(unrefed, true);
  await scheduled();
  assert.equal(manager.snapshot().token, 'new-token');

  manager.stop();
  assert.equal(cleared, timer);
});
