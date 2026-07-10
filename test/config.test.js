import test from 'node:test';
import assert from 'node:assert/strict';
import { loadConfig } from '../src/config.js';

test('loadConfig requires CATPAW_BASE_URL', () => {
  assert.throws(() => loadConfig({}), /CATPAW_BASE_URL/);
});

test('loadConfig normalizes base URL and defaults listen port', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'http://127.0.0.1:16326/',
    CATPAW_API_KEY: 'secret',
    CATPAW_MODEL: 'glm-4.5',
  });

  assert.equal(config.upstreamBaseUrl, 'http://127.0.0.1:16326');
  assert.equal(config.apiKey, 'secret');
  assert.equal(config.listenPort, 3000);
  assert.equal(config.model, 'glm-4.5');
  assert.equal(config.upstreamUrl, 'http://127.0.0.1:16326/v1/chat/completions');
});

test('loadConfig rejects invalid PORT', () => {
  assert.throws(() => loadConfig({
    CATPAW_BASE_URL: 'http://127.0.0.1:16326',
    PORT: 'nope',
  }), /PORT/);
});

test('loadConfig defaults Catpaw request compaction limits', () => {
  const config = loadConfig({ CATPAW_BASE_URL: 'https://catpaw.meituan.com' });

  assert.equal(config.maxSystemChars, 24000);
  assert.equal(config.maxToolDescriptionChars, 256);
});

test('loadConfig includes validated resource limits', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATAPI_MAX_REQUEST_BYTES: '2048',
  });

  assert.equal(config.resourceLimits.maxRequestBytes, 2048);
  assert.equal(config.resourceLimits.maxAgentSessions, 128);
});

test('loadConfig propagates invalid resource limit errors with the env key', () => {
  assert.throws(() => loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATAPI_MAX_LOG_BYTES: 'invalid',
  }), /CATAPI_MAX_LOG_BYTES/);
});

test('loadConfig defaults Claude Desktop session root from LOCALAPPDATA', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    LOCALAPPDATA: 'C:\\Users\\Administrator\\AppData\\Local',
  });

  assert.equal(
    config.claudeSessionRoot,
    'C:\\Users\\Administrator\\AppData\\Local\\Claude-3p\\local-agent-mode-sessions',
  );
});

test('loadConfig defaults usage history under LOCALAPPDATA', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.example',
    LOCALAPPDATA: 'C:\\Users\\test\\AppData\\Local',
  });

  assert.equal(
    config.usageStorePath,
    'C:\\Users\\test\\AppData\\Local\\Catapi\\usage.json',
  );
});

test('loadConfig supports CLAUDE_SESSION_ROOT override', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    LOCALAPPDATA: 'C:\\Users\\Administrator\\AppData\\Local',
    CLAUDE_SESSION_ROOT: 'D:\\claude-sessions',
  });

  assert.equal(config.claudeSessionRoot, 'D:\\claude-sessions');
});

test('loadConfig allows a full CATPAW_UPSTREAM_URL override', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'http://unused.local',
    CATPAW_UPSTREAM_URL: 'http://127.0.0.1:16326/proxy?target=https%3A%2F%2Fexample.test%2Fv1%2Fchat%2Fcompletions',
  });

  assert.equal(
    config.upstreamUrl,
    'http://127.0.0.1:16326/proxy?target=https%3A%2F%2Fexample.test%2Fv1%2Fchat%2Fcompletions',
  );
});

test('loadConfig reads Catpaw cookie and extra headers', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_COOKIE: 'passport=secret',
    CATPAW_HEADERS: '{"x-tenant":"external","x-client":"catapi"}',
  });

  assert.equal(config.cookie, 'passport=secret');
  assert.deepEqual(config.extraHeaders, {
    'x-tenant': 'external',
    'x-client': 'catapi',
  });
});

test('loadConfig maps Catpaw auth token and tenant to headers', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_AUTH_TOKEN: 'access-token',
    CATPAW_TENANT: '5282fa6645',
  });

  assert.deepEqual(config.extraHeaders, {
    'Catpaw-Auth': 'access-token',
    tenant: '5282fa6645',
  });
});

test('loadConfig enables debug logging from CATPAW_DEBUG', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_DEBUG: '1',
  });

  assert.equal(config.debug, true);
});

test('loadConfig forces streaming for Catpaw stream endpoint', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_UPSTREAM_URL: 'https://catpaw.meituan.com/api/gpt/openai/stream',
  });

  assert.equal(config.forceStream, true);
});

test('loadConfig enables native Agent protocol for the Catpaw stream endpoint', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_UPSTREAM_URL: 'https://catpaw.meituan.com/api/gpt/openai/stream',
  });

  assert.equal(config.nativeAgent, true);
  assert.equal(config.userModelTypeCode, 2);
});

test('loadConfig enables runtime Token refresh only when requested', () => {
  const disabled = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
  });
  const enabled = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_AUTO_REFRESH_TOKEN: '1',
  });

  assert.equal(disabled.autoRefreshToken, false);
  assert.equal(enabled.autoRefreshToken, true);
});

test('loadConfig maps Catpaw user id headers', () => {
  const config = loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_USER_MIS_ID: 'user-1',
  });

  assert.deepEqual(config.extraHeaders, {
    'user-mis-id': 'user-1',
    'user-uid': 'user-1',
    'mis-id': 'user-1',
  });
});

test('loadConfig rejects invalid CATPAW_HEADERS json', () => {
  assert.throws(() => loadConfig({
    CATPAW_BASE_URL: 'https://catpaw.meituan.com',
    CATPAW_HEADERS: 'not json',
  }), /CATPAW_HEADERS/);
});
