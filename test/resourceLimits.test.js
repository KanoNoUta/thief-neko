import test from 'node:test';
import assert from 'node:assert/strict';
import { loadResourceLimits } from '../src/resourceLimits.js';

const DEFAULT_RESOURCE_LIMITS = {
  maxAgentSessions: 128,
  agentSessionTtlMs: 6 * 60 * 60 * 1000,
  maxSuggestMappings: 256,
  maxRequestBytes: 10 * 1024 * 1024,
  maxStreamBufferChars: 4 * 1024 * 1024,
  upstreamTimeoutMs: 5 * 60 * 1000,
  maxRecentActivity: 100,
  maxLogBytes: 10 * 1024 * 1024,
  retainedLogFiles: 3,
  maxClaudeSessionFiles: 64,
};

test('loadResourceLimits returns all defaults', () => {
  assert.deepEqual(loadResourceLimits({}), DEFAULT_RESOURCE_LIMITS);
});

test('loadResourceLimits uses defaults for blank overrides', () => {
  assert.deepEqual(loadResourceLimits({
    CATAPI_MAX_AGENT_SESSIONS: '',
    CATAPI_AGENT_SESSION_TTL_MS: '   ',
  }), DEFAULT_RESOURCE_LIMITS);
});

test('loadResourceLimits reads valid overrides from their exact env keys', () => {
  assert.deepEqual(loadResourceLimits({
    CATAPI_MAX_AGENT_SESSIONS: '1',
    CATAPI_AGENT_SESSION_TTL_MS: '2',
    CATAPI_MAX_SUGGEST_MAPPINGS: '3',
    CATAPI_MAX_REQUEST_BYTES: '4',
    CATAPI_MAX_STREAM_BUFFER_CHARS: '5',
    CATAPI_UPSTREAM_TIMEOUT_MS: '6',
    CATAPI_MAX_RECENT_ACTIVITY: '7',
    CATAPI_MAX_LOG_BYTES: '8',
    CATAPI_RETAINED_LOG_FILES: '9',
    CATAPI_MAX_CLAUDE_SESSION_FILES: '10',
  }), {
    maxAgentSessions: 1,
    agentSessionTtlMs: 2,
    maxSuggestMappings: 3,
    maxRequestBytes: 4,
    maxStreamBufferChars: 5,
    upstreamTimeoutMs: 6,
    maxRecentActivity: 7,
    maxLogBytes: 8,
    retainedLogFiles: 9,
    maxClaudeSessionFiles: 10,
  });
});

test('loadResourceLimits accepts a trimmed maximum safe decimal integer', () => {
  const limits = loadResourceLimits({
    CATAPI_MAX_REQUEST_BYTES: ' 9007199254740991 ',
  });

  assert.equal(limits.maxRequestBytes, 9007199254740991);
});

for (const invalidValue of [
  '-1',
  'abc',
  '9007199254740992',
  '10mb',
  '0x10',
  '1e3',
  '+2',
]) {
  test(`loadResourceLimits rejects invalid override ${invalidValue}`, () => {
    assert.throws(
      () => loadResourceLimits({ CATAPI_MAX_LOG_BYTES: invalidValue }),
      /CATAPI_MAX_LOG_BYTES/,
    );
  });
}

test('loadResourceLimits rejects zero and names the env key', () => {
  assert.throws(
    () => loadResourceLimits({ CATAPI_MAX_AGENT_SESSIONS: '0' }),
    /CATAPI_MAX_AGENT_SESSIONS/,
  );
});

test('loadResourceLimits rejects fractional values and names the env key', () => {
  assert.throws(
    () => loadResourceLimits({ CATAPI_UPSTREAM_TIMEOUT_MS: '1.5' }),
    /CATAPI_UPSTREAM_TIMEOUT_MS/,
  );
});
