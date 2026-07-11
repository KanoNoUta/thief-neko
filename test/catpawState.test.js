import test from 'node:test';
import assert from 'node:assert/strict';
import { readCatpawSessionAsync } from '../src/catpawState.js';

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
