import { join } from 'node:path';
import { loadResourceLimits } from './resourceLimits.js';

const DEFAULT_MODEL = 'glm-5.2';
const DEFAULT_PORT = 3000;
const DEFAULT_MAX_SYSTEM_CHARS = 24000;
const DEFAULT_MAX_TOOL_DESCRIPTION_CHARS = 256;

export function loadConfig(env = process.env) {
  const upstreamBaseUrl = normalizeBaseUrl(env.CATPAW_BASE_URL);
  const upstreamUrl = normalizeUpstreamUrl(env.CATPAW_UPSTREAM_URL, upstreamBaseUrl);
  const listenPort = parsePort(env.PORT);
  const credentialBroker = parseCredentialBroker(env);
  const listenHost = env.HOST || '127.0.0.1';
  const inboundApiKey = String(env.CATAPI_API_KEY || '').trim();
  if (!isLoopbackHost(listenHost) && !inboundApiKey) {
    throw new Error('CATAPI_API_KEY is required when HOST is not a loopback address');
  }

  return {
    upstreamBaseUrl,
    upstreamUrl,
    apiKey: env.CATPAW_API_KEY || '',
    cookie: env.CATPAW_COOKIE || '',
    extraHeaders: buildExtraHeaders(env),
    model: env.CATPAW_MODEL || DEFAULT_MODEL,
    maxSystemChars: DEFAULT_MAX_SYSTEM_CHARS,
    maxToolDescriptionChars: DEFAULT_MAX_TOOL_DESCRIPTION_CHARS,
    resourceLimits: loadResourceLimits(env),
    listenHost,
    listenPort,
    inboundApiKey,
    debug: env.CATPAW_DEBUG === '1' || env.CATPAW_DEBUG === 'true',
    encrypt: env.CATPAW_ENCRYPT === '1' || env.CATPAW_ENCRYPT === 'true',
    forceStream: env.CATPAW_FORCE_STREAM === '1'
      || env.CATPAW_FORCE_STREAM === 'true'
      || upstreamUrl.includes('/stream'),
    nativeAgent: parseBoolean(
      env.CATPAW_NATIVE_AGENT,
      upstreamUrl.includes('/api/gpt/openai/stream'),
    ),
    autoRefreshToken: parseBoolean(env.CATPAW_AUTO_REFRESH_TOKEN, false),
    autoResetQuota: parseBoolean(env.CATPAW_AUTO_RESET_QUOTA, true),
    credentialPipe: credentialBroker.pipe,
    credentialNonce: credentialBroker.nonce,
    headlessSessionPath: String(env.CATPAW_SESSION_PATH || '').trim(),
    headlessSessionKeyPath: String(env.CATPAW_SESSION_KEY_PATH || '').trim(),
    tenant: String(env.CATPAW_TENANT || '').trim(),
    userModelTypeCode: parseModelType(env.CATPAW_MODEL_TYPE),
    claudeSessionRoot: resolveClaudeSessionRoot(env),
    usageStorePath: resolveUsageStorePath(env),
  };
}

function isLoopbackHost(host) {
  return host === '127.0.0.1' || host === '::1' || host === 'localhost';
}

function parseCredentialBroker(env) {
  const hasPipe = env.CATPAW_CREDENTIAL_PIPE !== undefined;
  const hasNonce = env.CATPAW_CREDENTIAL_NONCE !== undefined;
  if (!hasPipe && !hasNonce) {
    return { pipe: '', nonce: '' };
  }

  const pipe = String(env.CATPAW_CREDENTIAL_PIPE || '').trim();
  const nonce = String(env.CATPAW_CREDENTIAL_NONCE || '');
  if (!pipe || !nonce.trim()) {
    throw new Error(
      'CATPAW_CREDENTIAL_PIPE and CATPAW_CREDENTIAL_NONCE must be provided together and non-blank',
    );
  }
  return { pipe, nonce };
}

function resolveUsageStorePath(env) {
  if (env.CATAPI_USAGE_STORE_PATH) {
    return env.CATAPI_USAGE_STORE_PATH;
  }
  if (!env.LOCALAPPDATA) {
    return '';
  }
  return join(env.LOCALAPPDATA, 'Catapi', 'usage.json');
}

function resolveClaudeSessionRoot(env) {
  if (env.CLAUDE_SESSION_ROOT) {
    return env.CLAUDE_SESSION_ROOT;
  }

  if (!env.LOCALAPPDATA) {
    return '';
  }

  return join(env.LOCALAPPDATA, 'Claude-3p', 'local-agent-mode-sessions');
}

function parseBoolean(value, fallback) {
  if (value === undefined || value === '') {
    return fallback;
  }
  return value === '1' || value === 'true';
}

function parseModelType(value) {
  if (value === undefined || value === '') {
    return 2;
  }
  const modelType = Number(value);
  if (!Number.isInteger(modelType) || modelType < 0) {
    throw new Error('CATPAW_MODEL_TYPE must be a non-negative integer');
  }
  return modelType;
}

function normalizeBaseUrl(value) {
  if (!value || typeof value !== 'string' || value.trim() === '') {
    throw new Error('CATPAW_BASE_URL is required');
  }

  let parsed;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error('CATPAW_BASE_URL must be a valid URL');
  }

  if (!['http:', 'https:'].includes(parsed.protocol)) {
    throw new Error('CATPAW_BASE_URL must use http or https');
  }

  return value.trim().replace(/\/+$/, '');
}

function parsePort(value) {
  if (!value) {
    return DEFAULT_PORT;
  }

  const port = Number(value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error('PORT must be an integer from 1 to 65535');
  }

  return port;
}

function normalizeUpstreamUrl(value, upstreamBaseUrl) {
  if (!value) {
    return `${upstreamBaseUrl}/v1/chat/completions`;
  }

  try {
    const parsed = new URL(value);
    if (!['http:', 'https:'].includes(parsed.protocol)) {
      throw new Error();
    }
  } catch {
    throw new Error('CATPAW_UPSTREAM_URL must be a valid http or https URL');
  }

  return value.trim();
}

function buildExtraHeaders(env) {
  const headers = parseExtraHeaders(env.CATPAW_HEADERS);

  if (env.CATPAW_AUTH_TOKEN) {
    headers['Catpaw-Auth'] = String(env.CATPAW_AUTH_TOKEN);
  }

  if (env.CATPAW_TENANT) {
    headers.tenant = String(env.CATPAW_TENANT);
  }

  if (env.CATPAW_USER_MIS_ID) {
    const user = String(env.CATPAW_USER_MIS_ID);
    headers['user-mis-id'] = user;
    headers['user-uid'] = user;
    headers['mis-id'] = user;
  }

  return headers;
}

function parseExtraHeaders(value) {
  if (!value) {
    return {};
  }

  let parsed;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error('CATPAW_HEADERS must be valid JSON');
  }

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('CATPAW_HEADERS must be a JSON object');
  }

  return Object.fromEntries(
    Object.entries(parsed).map(([key, headerValue]) => [key, String(headerValue)]),
  );
}
