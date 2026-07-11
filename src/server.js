import http from 'node:http';
import { appendFile, mkdir } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { loadConfig } from './config.js';
import {
  anthropicToOpenAIRequest,
  normalizeOpenAIResponse,
  openAIToAnthropicMessage,
} from './converters.js';
import {
  AnthropicStreamBuilder,
  catpawStreamChunkToOpenAI,
  formatSseEvent,
  parseOpenAISseChunk,
} from './streaming.js';
import { decryptCatpawResponseBody, encryptCatpawRequestBody } from './catpawCrypto.js';
import {
  CatpawAgentSessionStore,
  buildCatpawAgentRequest,
  normalizeCatpawAgentChunk,
  summarizeCatpawToolCalls,
} from './catpawAgent.js';
import { resolveClaudeWorkspaceContext } from './claudeWorkspace.js';
import {
  CatpawCredentialManager,
  getCredentialSnapshot,
} from './catpawCredentials.js';
import { CredentialBroker } from './credentialBroker.js';
import { readCatpawSessionAsync } from './catpawState.js';
import { UsageStore, formatLocalDate, parseDateKey } from './usageStore.js';

export function createGatewayServer(config, dependencies = {}) {
  const limits = {
    maxAgentSessions: 128,
    agentSessionTtlMs: 6 * 60 * 60 * 1000,
    maxSuggestMappings: 256,
    maxRequestBytes: 10 * 1024 * 1024,
    maxStreamBufferChars: 4 * 1024 * 1024,
    upstreamTimeoutMs: 5 * 60 * 1000,
    maxRecentActivity: 100,
    ...config.resourceLimits,
  };
  const agentSessions = new CatpawAgentSessionStore({
    maxSessions: limits.maxAgentSessions,
    ttlMs: limits.agentSessionTtlMs,
    maxSuggestMappings: limits.maxSuggestMappings,
  });
  const usageStore = config.usageStore || new UsageStore(config.usageStorePath);
  const credentialProvider = dependencies.credentialProvider
    || dependencies.credentialManager
    || createCredentialProvider(config);
  credentialProvider?.start();
  const metrics = createGatewayMetrics(
    config,
    limits,
    agentSessions,
    usageStore,
    credentialProvider,
  );
  const server = http.createServer(async (req, res) => {
    try {
      await routeRequest(
        req,
        res,
        config,
        limits,
        agentSessions,
        metrics,
        credentialProvider,
      );
    } catch (error) {
      if (res.headersSent) {
        res.end();
        return;
      }
      sendJson(res, error.statusCode || 500, {
        type: 'error',
        error: { type: 'internal_error', message: error.message },
      });
    }
  });
  server.requestTimeout = limits.upstreamTimeoutMs + 30_000;
  server.headersTimeout = 30_000;
  server.keepAliveTimeout = 5_000;
  server.on('close', () => credentialProvider?.stop());
  return server;
}

async function routeRequest(
  req,
  res,
  config,
  limits,
  agentSessions,
  metrics,
  credentialProvider,
) {
  const url = new URL(req.url, `http://${req.headers.host || '127.0.0.1'}`);

  if (req.method === 'GET' && url.pathname === '/health') {
    sendJson(res, 200, { ok: true });
    return;
  }

  if (req.method === 'GET' && url.pathname === '/admin/status') {
    if (!isLoopback(config.listenHost || '127.0.0.1')) {
      sendJson(res, 404, { type: 'error', error: { type: 'not_found_error' } });
      return;
    }
    const range = resolveUsageRange(url.searchParams);
    await metrics.refreshQuota();
    const usage = await metrics.getUsage(range.start, range.end);
    sendJson(res, 200, metrics.snapshot(usage, range));
    return;
  }

  if (req.method === 'GET' && url.pathname === '/v1/models') {
    sendJson(res, 200, {
      data: [{ id: config.model, type: 'model', display_name: config.model }],
      has_more: false,
    });
    return;
  }

  if (req.method === 'POST' && url.pathname === '/v1/messages') {
    metrics.beginRequest();
    try {
      await handleMessages(
        req,
        res,
        config,
        limits,
        agentSessions,
        metrics,
        credentialProvider,
      );
      metrics.completeRequest(true);
    } catch (error) {
      metrics.completeRequest(false, error.statusCode || 500);
      throw error;
    }
    return;
  }

  sendJson(res, 404, {
    type: 'error',
    error: { type: 'not_found_error', message: `${req.method} ${url.pathname} not found` },
  });
}

async function handleMessages(
  req,
  res,
  config,
  limits,
  agentSessions,
  metrics,
  credentialProvider,
) {
  const anthropicRequest = await readJson(req, limits.maxRequestBytes);
  const workspaceContext = await resolveClaudeWorkspaceContext({
    root: config.claudeSessionRoot,
    headers: req.headers,
    metadata: anthropicRequest.metadata,
  });
  const openAIRequest = anthropicToOpenAIRequest(anthropicRequest, {
    model: config.model,
    maxSystemChars: config.maxSystemChars,
    maxToolDescriptionChars: config.maxToolDescriptionChars,
    workspaceContext,
  });
  const clientWantsStream = openAIRequest.stream;
  const useNativeAgent = config.nativeAgent;
  const agentSession = useNativeAgent ? agentSessions.get(openAIRequest) : null;
  if (agentSession && workspaceContext) {
    agentSession.workspaceContext = workspaceContext;
  }
  const upstreamRequest = useNativeAgent
    ? buildCatpawAgentRequest(openAIRequest, {
        conversationId: agentSession.conversationId,
        suggestUuidByToolCallId: agentSession.suggestUuidByToolCallId,
        userModelTypeCode: config.userModelTypeCode,
      })
    : config.forceStream
      ? { ...openAIRequest, stream: true }
      : openAIRequest;
  await writeDebugRecord(config, {
    type: 'request',
    stream: clientWantsStream,
    bodyBytes: Buffer.byteLength(JSON.stringify(openAIRequest)),
    messageCount: openAIRequest.messages?.length || 0,
    systemChars: openAIRequest.messages?.find((message) => message.role === 'system')?.content?.length || 0,
    toolCount: openAIRequest.tools?.length || 0,
    protocol: useNativeAgent ? 'catpaw-agent' : 'openai',
    workspaceMappingCount: workspaceContext?.mappings?.length || 0,
  });
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), limits.upstreamTimeoutMs);
  timeout.unref?.();
  let upstreamResponse;
  try {
    upstreamResponse = await fetchWithCredentialRefresh(
      config,
      upstreamRequest,
      credentialProvider,
      controller.signal,
    );
  } catch (error) {
    if (error.name === 'AbortError') {
      error.statusCode = 504;
      error.message = 'Catpaw upstream request timed out';
    }
    throw error;
  } finally {
    clearTimeout(timeout);
  }

  if (!upstreamResponse.ok) {
    await relayUpstreamError(
      res,
      upstreamResponse,
      Boolean(credentialProvider && upstreamResponse.status === 401),
    );
    return;
  }

  if (clientWantsStream) {
    await relayStreamingResponse(
      res,
      upstreamResponse,
      config,
      limits,
      agentSession,
      agentSessions,
      metrics,
    );
    return;
  }

  const contentType = upstreamResponse.headers.get('content-type') || '';
  if (contentType.includes('text/event-stream')) {
    const message = await collectStreamingMessage(
      upstreamResponse,
      config.model,
      limits,
      agentSession,
      agentSessions,
      metrics,
    );
    sendJson(res, 200, message);
    return;
  }

  const rawUpstreamBody = await upstreamResponse.text();
  const upstreamBody = decryptUpstreamBody(rawUpstreamBody, upstreamResponse);
  await writeDebugUpstream(config, upstreamResponse, upstreamBody);
  const openAIResponse = normalizeOpenAIResponse(JSON.parse(upstreamBody));
  sendJson(res, 200, openAIToAnthropicMessage(openAIResponse, config.model));
}

export function createCredentialProvider(config) {
  if (config.credentialPipe && config.credentialNonce) {
    return new CredentialBroker({
      pipeName: config.credentialPipe,
      nonce: config.credentialNonce,
    });
  }

  const token = config.extraHeaders?.['Catpaw-Auth'];
  if (!config.autoRefreshToken || !token) {
    return null;
  }
  return new CatpawCredentialManager({
    token,
    cookie: config.cookie,
    userMis: config.extraHeaders?.['user-mis-id'],
    readSession: () => readCatpawSessionAsync(),
    onRefresh: () => console.log('Catpaw credentials refreshed automatically'),
  });
}

function buildUpstreamHeaders(config, credential) {
  const headers = {
    'Content-Type': 'application/json',
    Accept: config.forceStream ? 'text/event-stream' : 'application/json',
    ...config.extraHeaders,
  };

  if (credential) {
    removeCredentialHeaders(headers);
  }

  if (config.forceStream) {
    headers['Cache-Control'] = 'no-cache';
    headers.Connection = 'keep-alive';
  }

  if (config.apiKey) {
    headers.authorization = `Bearer ${config.apiKey}`;
  }

  if (!credential && config.cookie) {
    headers.Cookie = config.cookie;
    headers['Catpaw-Cookie'] = config.cookie;
  }

  if (credential?.token) {
    headers['Catpaw-Auth'] = credential.token;
  }
  if (credential?.cookie) {
    headers.Cookie = credential.cookie;
    headers['Catpaw-Cookie'] = credential.cookie;
  }
  if (credential?.userMis) {
    headers['user-mis-id'] = credential.userMis;
    headers['user-uid'] = credential.userMis;
    headers['mis-id'] = credential.userMis;
  }

  return headers;
}

const CREDENTIAL_HEADER_NAMES = new Set([
  'authorization',
  'catpaw-auth',
  'cookie',
  'catpaw-cookie',
  'user-mis-id',
  'user-uid',
  'mis-id',
]);

function removeCredentialHeaders(headers) {
  for (const name of Object.keys(headers)) {
    if (CREDENTIAL_HEADER_NAMES.has(name.toLowerCase())) {
      delete headers[name];
    }
  }
}

async function fetchWithCredentialRefresh(
  config,
  upstreamRequest,
  credentialProvider,
  signal,
) {
  const sendAttempt = async () => {
    const credential = await getCredentialSnapshot(credentialProvider);
    const headers = buildUpstreamHeaders(config, credential);
    const body = buildUpstreamBody(upstreamRequest, headers, config);
    const response = await fetch(config.upstreamUrl, {
      method: 'POST',
      headers,
      body,
      signal,
    });
    return { response, token: credential?.token || '' };
  };

  const first = await sendAttempt();
  if (first.response.status !== 401 || !credentialProvider) {
    return first.response;
  }

  const changed = await credentialProvider.refreshAfterUnauthorized(first.token);
  if (!changed) {
    return first.response;
  }

  await first.response.body?.cancel();
  return (await sendAttempt()).response;
}

function buildUpstreamBody(openAIRequest, headers, config) {
  const body = JSON.stringify(openAIRequest);
  if (!config.encrypt) {
    return body;
  }

  return encryptCatpawRequestBody(body, headers);
}

function decryptUpstreamBody(body, upstreamResponse) {
  const encryptedKey = upstreamResponse.headers.get('encrypted-key');
  if (!encryptedKey) {
    return body;
  }

  return decryptCatpawResponseBody(body, encryptedKey);
}

async function relayStreamingResponse(
  res,
  upstreamResponse,
  config,
  limits,
  agentSession,
  agentSessions,
  metrics,
) {
  await writeDebugRecord(config, {
    type: 'upstream_response',
    status: upstreamResponse.status,
    contentType: upstreamResponse.headers.get('content-type') || '',
    encrypted: Boolean(upstreamResponse.headers.get('encrypted-key')),
  });
  res.writeHead(200, {
    'content-type': 'text/event-stream; charset=utf-8',
    'cache-control': 'no-cache, no-transform',
    connection: 'keep-alive',
  });

  const contentType = upstreamResponse.headers.get('content-type') || '';
  if (!contentType.includes('text/event-stream')) {
    const openAIResponse = normalizeOpenAIResponse(await upstreamResponse.json());
    const message = openAIToAnthropicMessage(openAIResponse, config.model);
    writeMessageAsStream(res, message);
    res.end();
    return;
  }

  const builder = new AnthropicStreamBuilder(config.model, {
    collapseRepeatedText: Boolean(agentSession),
    maxBufferChars: limits.maxStreamBufferChars,
  });
  const decoder = new TextDecoder();
  let buffer = '';
  let emitted = 0;

  for await (const chunk of upstreamResponse.body) {
    buffer += decoder.decode(chunk, { stream: true });
    enforceStreamBufferLimit(buffer, limits.maxStreamBufferChars);
    const parts = buffer.split(/\r?\n\r?\n/);
    buffer = parts.pop() || '';

    for (const part of parts) {
      for (const parsed of parseOpenAISseChunk(`${part}\n\n`)) {
        metrics.captureQuota(parsed);
        await writeDebugStreamChunk(config, parsed);
        builder.ingest(normalizeStreamChunk(parsed, agentSession, agentSessions));
        emitted = writeNewEvents(res, builder.events, emitted);
      }
    }
  }

  if (buffer.trim()) {
    for (const parsed of parseOpenAISseChunk(`${buffer}\n\n`)) {
      metrics.captureQuota(parsed);
      await writeDebugStreamChunk(config, parsed);
      builder.ingest(normalizeStreamChunk(parsed, agentSession, agentSessions));
      emitted = writeNewEvents(res, builder.events, emitted);
    }
  }

  builder.finish();
  await metrics.captureUsage(builder);
  writeNewEvents(res, builder.events, emitted);
  res.end();
}

async function collectStreamingMessage(
  upstreamResponse,
  model,
  limits,
  agentSession,
  agentSessions,
  metrics,
) {
  const builder = new AnthropicStreamBuilder(model, {
    collapseRepeatedText: Boolean(agentSession),
    maxBufferChars: limits.maxStreamBufferChars,
  });
  const decoder = new TextDecoder();
  let buffer = '';

  for await (const chunk of upstreamResponse.body) {
    buffer += decoder.decode(chunk, { stream: true });
    enforceStreamBufferLimit(buffer, limits.maxStreamBufferChars);
    const parts = buffer.split(/\r?\n\r?\n/);
    buffer = parts.pop() || '';

    for (const part of parts) {
      for (const parsed of parseOpenAISseChunk(`${part}\n\n`)) {
        metrics.captureQuota(parsed);
        builder.ingest(normalizeStreamChunk(parsed, agentSession, agentSessions));
      }
    }
  }

  if (buffer.trim()) {
    for (const parsed of parseOpenAISseChunk(`${buffer}\n\n`)) {
      metrics.captureQuota(parsed);
      builder.ingest(normalizeStreamChunk(parsed, agentSession, agentSessions));
    }
  }

  builder.finish();
  await metrics.captureUsage(builder);
  return anthropicEventsToMessage(builder.events, model);
}

function normalizeStreamChunk(chunk, agentSession, agentSessions) {
  const normalized = catpawStreamChunkToOpenAI(chunk);
  if (!agentSession) {
    return normalized;
  }

  agentSessions.record(agentSession, chunk);
  return normalizeCatpawAgentChunk(normalized, agentSession.workspaceContext);
}

function anthropicEventsToMessage(events, model) {
  const start = events.find((event) => event.event === 'message_start');
  const message = start?.data?.message || {
    id: `msg_${Date.now()}`,
    type: 'message',
    role: 'assistant',
  };
  const content = [];
  const blocks = new Map();
  let stopReason = 'end_turn';
  let usage = { input_tokens: 0, output_tokens: 0 };

  for (const event of events) {
    if (event.event === 'content_block_start') {
      const block = { ...event.data.content_block };
      blocks.set(event.data.index, block);
      content[event.data.index] = block;
    }

    if (event.event === 'content_block_delta') {
      const block = blocks.get(event.data.index);
      if (block?.type === 'text') {
        block.text += event.data.delta.text || '';
      }
    }

    if (event.event === 'message_delta') {
      stopReason = event.data.delta?.stop_reason || stopReason;
      usage = {
        input_tokens: usage.input_tokens,
        output_tokens: event.data.usage?.output_tokens || usage.output_tokens,
      };
    }
  }

  return {
    ...message,
    model,
    content: content.filter(Boolean),
    stop_reason: stopReason,
    stop_sequence: null,
    usage,
  };
}

function writeMessageAsStream(res, message) {
  res.write(formatSseEvent({
    event: 'message_start',
    data: {
      type: 'message_start',
      message: { ...message, content: [], stop_reason: null, stop_sequence: null },
    },
  }));

  message.content.forEach((contentBlock, index) => {
    res.write(formatSseEvent({
      event: 'content_block_start',
      data: { type: 'content_block_start', index, content_block: emptyBlock(contentBlock) },
    }));

    if (contentBlock.type === 'text' && contentBlock.text) {
      res.write(formatSseEvent({
        event: 'content_block_delta',
        data: {
          type: 'content_block_delta',
          index,
          delta: { type: 'text_delta', text: contentBlock.text },
        },
      }));
    }

    res.write(formatSseEvent({
      event: 'content_block_stop',
      data: { type: 'content_block_stop', index },
    }));
  });

  res.write(formatSseEvent({
    event: 'message_delta',
    data: {
      type: 'message_delta',
      delta: { stop_reason: message.stop_reason, stop_sequence: null },
      usage: { output_tokens: message.usage.output_tokens },
    },
  }));
  res.write(formatSseEvent({ event: 'message_stop', data: { type: 'message_stop' } }));
}

function emptyBlock(contentBlock) {
  if (contentBlock.type === 'text') {
    return { type: 'text', text: '' };
  }

  return contentBlock;
}

function writeNewEvents(res, events, emitted) {
  for (const event of events.slice(emitted)) {
    res.write(formatSseEvent(event));
  }

  return events.length;
}

async function relayUpstreamError(res, upstreamResponse, temporaryAuthFailure = false) {
  const rawBody = await upstreamResponse.text();
  const body = decryptUpstreamBody(rawBody, upstreamResponse);
  sendJson(res, temporaryAuthFailure ? 503 : upstreamResponse.status, {
    type: 'error',
    error: {
      type: temporaryAuthFailure ? 'upstream_auth_refresh_pending' : 'upstream_error',
      message: body || upstreamResponse.statusText,
    },
  });
}

async function writeDebugUpstream(config, upstreamResponse, body) {
  if (!config.debug) {
    return;
  }

  await mkdir('logs', { recursive: true });
  await appendFile('logs/upstream-debug.log', JSON.stringify({
    at: new Date().toISOString(),
    status: upstreamResponse.status,
    contentType: upstreamResponse.headers.get('content-type') || '',
    body,
  }) + '\n');
}

async function writeDebugStreamChunk(config, chunk) {
  const choice = chunk?.choices?.[0] || {};
  const delta = choice.delta || {};
  await writeDebugRecord(config, {
    type: 'stream_chunk',
    chunkType: chunk === null ? 'null' : Array.isArray(chunk) ? 'array' : typeof chunk,
    topLevelKeys: chunk && typeof chunk === 'object' ? Object.keys(chunk) : [],
    id: chunk.id || null,
    status: chunk?.status ?? null,
    statusCode: chunk?.statusCode ?? null,
    lastOne: chunk?.lastOne ?? null,
    code: chunk?.code ?? chunk?.error?.code ?? null,
    errorType: chunk?.error?.type ?? null,
    message: typeof chunk?.message === 'string'
      ? chunk.message.slice(0, 300)
      : typeof chunk?.error?.message === 'string'
        ? chunk.error.message.slice(0, 300)
        : null,
    stringChars: typeof chunk === 'string' ? chunk.length : 0,
    msgChars: typeof chunk?.msg === 'string' ? chunk.msg.length : 0,
    upstreamError: chunk?.statusCode && typeof chunk?.msg === 'string'
      ? chunk.msg.slice(0, 500)
      : null,
    deltaKeys: Object.keys(delta),
    topContentChars: typeof chunk?.content === 'string' ? chunk.content.length : 0,
    contentChars: typeof delta.content === 'string' ? delta.content.length : 0,
    reasoningChars: typeof delta.reasoning_content === 'string' ? delta.reasoning_content.length : 0,
    toolCallCount: Array.isArray(delta.tool_calls) ? delta.tool_calls.length : 0,
    toolCalls: summarizeCatpawToolCalls(chunk),
    finishReason: choice.finish_reason || choice.finishReason || null,
  });
}

async function writeDebugRecord(config, record) {
  if (!config.debug) {
    return;
  }

  await mkdir('logs', { recursive: true });
  await appendFile('logs/gateway-diagnostic.jsonl', `${JSON.stringify({
    at: new Date().toISOString(),
    ...record,
  })}\n`);
}

async function readJson(req, maxBytes = 10 * 1024 * 1024) {
  const chunks = [];
  let bytes = 0;

  for await (const chunk of req) {
    bytes += chunk.length;
    if (bytes > maxBytes) {
      const error = new Error(`Request body exceeds ${maxBytes} bytes`);
      error.statusCode = 413;
      throw error;
    }
    chunks.push(chunk);
  }

  if (chunks.length === 0) {
    return {};
  }

  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function enforceStreamBufferLimit(buffer, maximum) {
  if (buffer.length <= maximum) {
    return;
  }
  const error = new RangeError(`Stream exceeded maximum of ${maximum} retained characters`);
  error.code = 'CATAPI_STREAM_BUFFER_LIMIT';
  error.statusCode = 502;
  throw error;
}

function isLoopback(host) {
  return host === '127.0.0.1' || host === '::1' || host === 'localhost';
}

function createGatewayMetrics(
  config,
  limits,
  agentSessions,
  usageStore,
  credentialProvider,
) {
  const startedAt = Date.now();
  const quotaUrl = config.upstreamBaseUrl
    ? `${config.upstreamBaseUrl.replace(/\/+$/, '')}/api/user/limit`
    : null;
  let quotaLastFetchedAt = 0;
  let quotaRequest = null;
  const state = {
    active: 0,
    successful: 0,
    failed: 0,
    inputTokens: null,
    outputTokens: null,
    quota: { remaining: null, used: null, total: null },
    activity: [],
  };

  const addActivity = (type, status) => {
    state.activity.unshift({ at: new Date().toISOString(), type, status });
    state.activity.length = Math.min(state.activity.length, limits.maxRecentActivity);
  };

  return {
    beginRequest() {
      state.active += 1;
    },
    completeRequest(success, status = success ? 200 : 500) {
      state.active = Math.max(0, state.active - 1);
      state[success ? 'successful' : 'failed'] += 1;
      addActivity(success ? 'request_completed' : 'request_failed', status);
    },
    async captureUsage(builder) {
      const usage = {};
      if (builder.hasInputUsage) {
        state.inputTokens = (state.inputTokens || 0) + builder.usage.input_tokens;
        usage.inputTokens = builder.usage.input_tokens;
      }
      if (builder.hasOutputUsage) {
        state.outputTokens = (state.outputTokens || 0) + builder.usage.output_tokens;
        usage.outputTokens = builder.usage.output_tokens;
      }
      if (Object.keys(usage).length > 0) {
        await usageStore.record(usage);
      }
    },
    getUsage(start, end) {
      return usageStore.sumRange(start, end);
    },
    captureQuota(chunk) {
      const quota = extractQuotaSnapshot(chunk);
      for (const key of ['remaining', 'used', 'total']) {
        if (quota[key] !== null) {
          state.quota[key] = quota[key];
        }
      }
    },
    async refreshQuota() {
      if (!quotaUrl || Date.now() - quotaLastFetchedAt < 30_000) {
        return;
      }
      if (quotaRequest) {
        await quotaRequest;
        return;
      }

      quotaRequest = (async () => {
        quotaLastFetchedAt = Date.now();
        try {
          const credential = await getCredentialSnapshot(credentialProvider);
          const headers = buildUpstreamHeaders(
            { ...config, forceStream: false },
            credential,
          );
          headers.Accept = 'application/json';
          const response = await fetch(quotaUrl, {
            method: 'GET',
            headers,
            signal: AbortSignal.timeout(5_000),
          });
          if (!response.ok) {
            return;
          }
          const rawBody = await response.text();
          const body = decryptUpstreamBody(rawBody, response);
          const quota = extractQuotaSnapshot(JSON.parse(body));
          for (const key of ['remaining', 'used', 'total']) {
            if (quota[key] !== null) {
              state.quota[key] = quota[key];
            }
          }
        } catch {
          // Status polling remains available when Catpaw's quota endpoint is temporarily unavailable.
        } finally {
          quotaRequest = null;
        }
      })();
      await quotaRequest;
    },
    snapshot(usage, range) {
      const memory = process.memoryUsage();
      return {
        ok: true,
        model: config.model,
        host: config.listenHost || '127.0.0.1',
        port: config.listenPort || 3000,
        pid: process.pid,
        uptimeSeconds: Math.floor((Date.now() - startedAt) / 1000),
        memory: {
          rssBytes: memory.rss,
          heapUsedBytes: memory.heapUsed,
          heapTotalBytes: memory.heapTotal,
        },
        sessions: { active: agentSessions.size, maximum: limits.maxAgentSessions },
        requests: {
          active: state.active,
          successful: state.successful,
          failed: state.failed,
        },
        usage: { ...usage, start: range.start, end: range.end },
        quota: state.quota,
        recentActivity: state.activity,
      };
    },
  };
}

function resolveUsageRange(searchParams, now = new Date()) {
  const rawStart = searchParams.get('start');
  const rawEnd = searchParams.get('end');
  if ((rawStart && !rawEnd) || (!rawStart && rawEnd)) {
    const error = new RangeError('start and end dates must be provided together');
    error.statusCode = 400;
    throw error;
  }

  const start = rawStart || formatLocalDate(now);
  const end = rawEnd || start;
  let startDate;
  let endDate;
  try {
    startDate = parseDateKey(start, 'start date');
    endDate = parseDateKey(end, 'end date');
  } catch (error) {
    error.statusCode = 400;
    throw error;
  }
  if (end < start) {
    const error = new RangeError('end date cannot be before start date');
    error.statusCode = 400;
    throw error;
  }
  const startDay = Date.UTC(startDate.getFullYear(), startDate.getMonth(), startDate.getDate());
  const endDay = Date.UTC(endDate.getFullYear(), endDate.getMonth(), endDate.getDate());
  if ((endDay - startDay) / 86_400_000 + 1 > 731) {
    const error = new RangeError('usage date range cannot exceed two years');
    error.statusCode = 400;
    throw error;
  }
  return { start, end };
}

function extractQuotaSnapshot(payload) {
  const candidates = [
    payload?.data?.quota,
    payload?.quota,
    payload?.data,
    payload,
  ].filter((value) => value && typeof value === 'object');

  for (const quota of candidates) {
    const used = firstQuotaNumber(quota, ['modelRequestCount', 'modelRequestTotalCount', 'used']);
    const total = firstQuotaNumber(quota, ['modelRequestLimit', 'modelRequestLimitCount', 'total']);
    let remaining = firstQuotaNumber(quota, [
      'modelRemaingCount',
      'modelRemainingCount',
      'remaining',
    ]);
    if (remaining === null && used !== null && total !== null) {
      remaining = Math.max(0, total - used);
    }
    if (remaining !== null || used !== null || total !== null) {
      return { remaining, used, total };
    }
  }

  return { remaining: null, used: null, total: null };
}

function firstQuotaNumber(source, keys) {
  for (const key of keys) {
    const value = source[key];
    if (typeof value === 'number' && Number.isFinite(value) && value >= 0) {
      return value;
    }
  }
  return null;
}

function sendJson(res, status, payload) {
  if (res.headersSent) {
    return;
  }

  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(payload));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const config = loadConfig();
  const server = createGatewayServer(config);

  server.listen(config.listenPort, config.listenHost, () => {
    console.log(
      `catpaw-claude-code-gateway listening on http://${config.listenHost}:${config.listenPort}`,
    );
    console.log(`upstream: ${config.upstreamUrl}`);
    console.log(`model: ${config.model}`);
  });
}
