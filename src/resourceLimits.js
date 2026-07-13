const RESOURCE_LIMIT_DEFINITIONS = [
  ['maxAgentSessions', 'CATAPI_MAX_AGENT_SESSIONS', 128],
  ['agentSessionTtlMs', 'CATAPI_AGENT_SESSION_TTL_MS', 6 * 60 * 60 * 1000],
  ['maxSuggestMappings', 'CATAPI_MAX_SUGGEST_MAPPINGS', 256],
  ['maxRequestBytes', 'CATAPI_MAX_REQUEST_BYTES', 10 * 1024 * 1024],
  ['maxHistoryChars', 'CATAPI_MAX_HISTORY_CHARS', 256 * 1024],
  ['maxStreamBufferChars', 'CATAPI_MAX_STREAM_BUFFER_CHARS', 4 * 1024 * 1024],
  ['upstreamTimeoutMs', 'CATAPI_UPSTREAM_TIMEOUT_MS', 5 * 60 * 1000],
  ['maxRecentActivity', 'CATAPI_MAX_RECENT_ACTIVITY', 100],
  ['maxLogBytes', 'CATAPI_MAX_LOG_BYTES', 10 * 1024 * 1024],
  ['retainedLogFiles', 'CATAPI_RETAINED_LOG_FILES', 3],
  ['maxClaudeSessionFiles', 'CATAPI_MAX_CLAUDE_SESSION_FILES', 64],
];

export function loadResourceLimits(env = process.env) {
  return Object.fromEntries(RESOURCE_LIMIT_DEFINITIONS.map(([name, envKey, defaultValue]) => [
    name,
    parsePositiveSafeInteger(env[envKey], envKey, defaultValue),
  ]));
}

function parsePositiveSafeInteger(value, envKey, defaultValue) {
  if (value === undefined) {
    return defaultValue;
  }

  const normalizedValue = String(value).trim();
  if (normalizedValue === '') {
    return defaultValue;
  }

  if (!/^\d+$/.test(normalizedValue)) {
    throw new Error(`${envKey} must be a positive safe integer`);
  }

  const parsed = Number(normalizedValue);
  if (!Number.isSafeInteger(parsed) || parsed <= 0) {
    throw new Error(`${envKey} must be a positive safe integer`);
  }

  return parsed;
}
