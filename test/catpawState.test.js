import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { readCatpawSession, readCatpawSessionAsync } from '../src/catpawState.js';

test('readCatpawSession extracts credentials and account from a temporary SQLite state', async () => {
  const appData = await mkdtemp(path.join(os.tmpdir(), 'catpaw-state-test-'));
  const storageDirectory = path.join(
    appData,
    'CatPawAI',
    'User',
    'globalStorage',
  );
  await mkdir(storageDirectory, { recursive: true });
  const database = new DatabaseSync(path.join(storageDirectory, 'state.vscdb'));

  try {
    database.exec('create table ItemTable (key text primary key, value text)');
    const authentication = JSON.stringify({
      refreshToken: 'fixture-refresh',
      sessions: [{
        accessToken: 'fixture-access',
        account: { id: 'fixture-user', label: 'Fixture Account' },
      }],
    });
    database.prepare('insert into ItemTable (key, value) values (?, ?)').run(
      'catpaw.mt-authentication',
      JSON.stringify({ 'mt.auth': authentication }),
    );
    database.close();

    assert.deepEqual(readCatpawSession({ APPDATA: appData }), {
      token: 'fixture-access',
      refreshToken: 'fixture-refresh',
      userMis: 'fixture-user',
      accountLabel: 'Fixture Account',
    });
  } finally {
    try {
      database.close();
    } catch {
      // Already closed before the reader opens the database.
    }
    await rm(appData, { recursive: true, force: true });
  }
});

test('readCatpawSessionAsync reads state through an asynchronous helper process', async () => {
  const env = { APPDATA: 'C:\\Users\\test\\AppData\\Roaming' };
  let receivedEnv;
  const result = await readCatpawSessionAsync(env, async (value) => {
    receivedEnv = value;
    return {
      stdout: '{"token":"fresh-token","refreshToken":"refresh-token","userMis":"user-1","accountLabel":"Catpaw User"}',
    };
  });

  assert.equal(receivedEnv, env);
  assert.deepEqual(result, {
    token: 'fresh-token',
    refreshToken: 'refresh-token',
    userMis: 'user-1',
    accountLabel: 'Catpaw User',
  });
});

test('readCatpawSessionAsync rejects incomplete helper output', async () => {
  await assert.rejects(
    () => readCatpawSessionAsync({}, async () => ({
      stdout: '{"token":"token","refreshToken":"","userMis":"user","accountLabel":"User"}',
    })),
    /incomplete/,
  );
});
