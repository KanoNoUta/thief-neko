import { createHash, randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { rewriteClaudeFileToolCall } from './claudeWorkspace.js';
import { repairTruncatedJsonArguments } from './converters.js';

const DEFAULT_MODEL_TYPE = 2;
const TRIGGER_MODE = 'AGENT';
const monotonicNow = performance.now.bind(performance);
const CATPAW_INTERNAL_TOOLS = [
  'codebase_search',
  'grep',
  'glob_file_search',
  'read_file',
  'fetch_rules',
  'list_dir',
  'km_search',
  'web_search',
  'web_fetch',
  'MultiEdit',
  'write',
  'string_replace',
  'delete_file',
  'read_lints',
  'run_terminal_cmd',
  'catpaw_deploy',
  'fetch_pull_request',
  'deploy_project',
  'todo_write',
  'update_memory',
  'create_plan',
  'AskQuestion',
  'task',
];

export function buildCatpawAgentRequest(openAIRequest, options = {}) {
  const userModelTypeCode = options.userModelTypeCode ?? DEFAULT_MODEL_TYPE;
  const messages = (openAIRequest.messages || []).filter((message) => message.role !== 'system');
  const toolNames = collectToolNames(messages);

  return {
    messages: messages.map((message) => toCatpawMessage(
      message,
      toolNames,
      options.suggestUuidByToolCallId,
    )),
    filePath: options.filePath || '',
    conversationId: options.conversationId || randomUUID(),
    triggerMode: TRIGGER_MODE,
    userModelTypeCode,
    promptEditorSubmitData: '{}',
    agentModeConfig: buildAgentModeConfig(openAIRequest, userModelTypeCode),
  };
}

export function normalizeCatpawAgentChunk(chunk, workspaceContext) {
  if (!chunk || typeof chunk !== 'object') {
    return chunk;
  }

  const existingChoice = chunk.choices?.[0] || {};
  const delta = { ...(existingChoice.delta || {}) };
  const hasNativeDelta = Object.keys(delta).length > 0;
  if (
    typeof chunk.content === 'string'
    && chunk.content !== '[DONE]'
    && delta.content === undefined
    && !hasNativeDelta
  ) {
    delta.content = sanitizeModelText(chunk.content);
  } else if (typeof delta.content === 'string') {
    delta.content = sanitizeModelText(delta.content);
  }

  const streamedToolCalls = firstToolCallArray(
    chunk.toolCalls,
    delta.tool_calls,
    delta.toolCalls,
  );
  if (streamedToolCalls) {
    delta.tool_calls = streamedToolCalls.map((toolCall, index) => {
      const normalizedToolCall = {
        ...toolCall,
        index,
        function: toolCall.function
          ? {
              ...toolCall.function,
              arguments: toolCall.function.arguments == null
                ? toolCall.function.arguments
                : normalizeArguments(toolCall.function.arguments, toolCall.function.name),
            }
          : toolCall.function,
      };
      return rewriteClaudeFileToolCall(normalizedToolCall, workspaceContext);
    });
    delete delta.toolCalls;
  }

  const nativeFinishReason = existingChoice.finish_reason || existingChoice.finishReason;
  const finishReason = nativeFinishReason
    || (chunk.lastOne ? (delta.tool_calls?.length ? 'tool_calls' : 'stop') : null);

  return {
    ...chunk,
    choices: [{
      ...existingChoice,
      delta,
      finish_reason: finishReason,
    }],
  };
}

function firstToolCallArray(...candidates) {
  return candidates.find((candidate) => Array.isArray(candidate));
}

export function summarizeCatpawToolCalls(chunk) {
  const sources = [
    ['topLevel', chunk?.toolCalls],
    ['deltaCamel', chunk?.choices?.[0]?.delta?.toolCalls],
    ['deltaSnake', chunk?.choices?.[0]?.delta?.tool_calls],
  ];
  const summary = [];

  for (const [source, toolCalls] of sources) {
    if (!Array.isArray(toolCalls)) {
      continue;
    }

    toolCalls.forEach((toolCall, index) => {
      const fn = toolCall?.function;
      const argumentsPresent = Boolean(
        fn && Object.prototype.hasOwnProperty.call(fn, 'arguments'),
      );
      const value = argumentsPresent ? fn.arguments : undefined;
      summary.push({
        source,
        index,
        id: toolCall?.id || null,
        name: fn?.name || null,
        argumentsPresent,
        argumentsType: value === null ? 'null' : typeof value,
        argumentsChars: typeof value === 'string' ? value.length : 0,
      });
    });
  }

  return summary;
}

export class CatpawAgentSessionStore {
  constructor({
    maxSessions = 128,
    ttlMs = 21_600_000,
    maxSuggestMappings = 256,
    maxRequestsPerConversation = 64,
    now = monotonicNow,
  } = {}) {
    validatePositiveSafeInteger('maxSessions', maxSessions);
    validatePositiveSafeInteger('ttlMs', ttlMs);
    validatePositiveSafeInteger('maxSuggestMappings', maxSuggestMappings);
    validatePositiveSafeInteger('maxRequestsPerConversation', maxRequestsPerConversation);
    if (typeof now !== 'function') {
      throw new TypeError('now must be a function');
    }

    this.maxSessions = maxSessions;
    this.ttlMs = ttlMs;
    this.maxSuggestMappings = maxSuggestMappings;
    this.maxRequestsPerConversation = maxRequestsPerConversation;
    this.now = now;
    this.sessions = new Map();
  }

  get size() {
    return this.sessions.size;
  }

  get(openAIRequest) {
    const now = this.#readNow();
    this.#sweepAt(now);
    const key = sessionKey(openAIRequest);
    let session = this.sessions.get(key);
    if (session) {
      if (session.requestCount >= this.maxRequestsPerConversation) {
        session.conversationId = randomUUID();
        session.suggestUuidByToolCallId.clear();
        session.requestCount = 0;
        session.rotationCount += 1;
      }
      session.lastAccessAt = now;
      this.sessions.delete(key);
      this.sessions.set(key, session);
    } else {
      session = {
        conversationId: randomUUID(),
        suggestUuidByToolCallId: new Map(),
        lastAccessAt: now,
        requestCount: 0,
        rotationCount: 0,
      };
      this.sessions.set(key, session);
    }

    while (this.sessions.size > this.maxSessions) {
      this.sessions.delete(this.sessions.keys().next().value);
    }

    session.requestCount += 1;
    return session;
  }

  sweep() {
    return this.#sweepAt(this.#readNow());
  }

  #sweepAt(now) {
    const expiresAt = now - this.ttlMs;
    let removed = 0;

    for (const [key, session] of this.sessions) {
      if (session.lastAccessAt <= expiresAt) {
        this.sessions.delete(key);
        removed += 1;
      }
    }

    return removed;
  }

  #readNow() {
    const now = this.now();
    if (typeof now !== 'number' || !Number.isFinite(now)) {
      throw new TypeError('now must return a finite number');
    }
    return now;
  }

  record(session, chunk) {
    if (!session || !chunk?.suggestUuid || !Array.isArray(chunk.toolCalls)) {
      return;
    }

    for (const toolCall of chunk.toolCalls) {
      if (toolCall?.id) {
        session.suggestUuidByToolCallId.delete(toolCall.id);
        session.suggestUuidByToolCallId.set(toolCall.id, chunk.suggestUuid);
        while (session.suggestUuidByToolCallId.size > this.maxSuggestMappings) {
          session.suggestUuidByToolCallId.delete(
            session.suggestUuidByToolCallId.keys().next().value,
          );
        }
      }
    }
  }
}

function validatePositiveSafeInteger(name, value) {
  if (typeof value !== 'number') {
    throw new TypeError(`${name} must be a number`);
  }
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new RangeError(`${name} must be a positive safe integer`);
  }
}

function buildAgentModeConfig(openAIRequest, userModelTypeCode) {
  const systemPrompt = (openAIRequest.messages || [])
    .filter((message) => message.role === 'system')
    .map((message) => messageContentText(message.content))
    .join('\n\n');

  return {
    id: 'claude-desktop-agent',
    type: 'CUSTOM_AGENT',
    name: 'Claude Desktop',
    icon: 'sparkle',
    defaultDocs: [],
    selectedSubAgents: [],
    agentDescription: '',
    tools: [
      ...CATPAW_INTERNAL_TOOLS.map((toolUseName) => ({ toolUseName, enable: false })),
      {
        toolUseName: 'use_mcp_tool',
        enable: true,
        mcpTools: openAIRequest.tools || [],
      },
    ],
    model: {
      default: userModelTypeCode,
      maxMode: true,
      autoMode: false,
    },
    systemPrompt,
    sendProjectTree: true,
    restoreVisible: true,
    evaluationMode: true,
    enableProjectLayout: true,
    sourceType: 'local',
    const: {
      trigger: TRIGGER_MODE,
      applyTrigger: 'AGENT_APPLY',
    },
  };
}

function toCatpawMessage(message, toolNames, suggestUuidByToolCallId = new Map()) {
  const content = messageContentText(message.content);
  const mapped = {
    content,
    multiModalContent: [{ type: 'text', text: content }],
    role: message.role,
    triggerMode: TRIGGER_MODE,
  };

  if (Array.isArray(message.tool_calls) && message.tool_calls.length > 0) {
    mapped.tool_calls = message.tool_calls;
    const suggestUuid = firstSuggestUuid(message.tool_calls, suggestUuidByToolCallId);
    if (suggestUuid) {
      mapped.suggestUuid = suggestUuid;
    }
  }

  if (message.role === 'tool') {
    mapped.tool_call_id = message.tool_call_id;
    mapped.tool_call_name = toolNames.get(message.tool_call_id) || 'unknown';
    const suggestUuid = suggestUuidByToolCallId.get(message.tool_call_id);
    if (suggestUuid) {
      mapped.suggestUuid = suggestUuid;
    }
  }

  return mapped;
}

function messageContentText(content) {
  if (content === undefined || content === null) {
    return '';
  }
  if (typeof content === 'string') {
    return content;
  }
  if (Array.isArray(content)) {
    return content
      .map((block) => messageContentText(block))
      .filter(Boolean)
      .join('\n');
  }
  if (typeof content === 'object') {
    if (typeof content.text === 'string') {
      return content.text;
    }
    if (Object.prototype.hasOwnProperty.call(content, 'content')) {
      return messageContentText(content.content);
    }
    if (['image', 'image_url', 'input_image', 'document'].includes(content.type)) {
      return '';
    }
    try {
      return JSON.stringify(content);
    } catch {
      return '';
    }
  }
  return String(content);
}

function collectToolNames(messages) {
  const names = new Map();
  for (const message of messages) {
    for (const toolCall of message.tool_calls || []) {
      if (toolCall?.id) {
        names.set(toolCall.id, toolCall.function?.name || 'unknown');
      }
    }
  }
  return names;
}

function firstSuggestUuid(toolCalls, suggestUuidByToolCallId) {
  for (const toolCall of toolCalls) {
    const suggestUuid = suggestUuidByToolCallId.get(toolCall.id);
    if (suggestUuid) {
      return suggestUuid;
    }
  }
  return undefined;
}

function normalizeArguments(value, toolName) {
  if (typeof value === 'string') {
    const repaired = repairTruncatedJsonArguments(value);
    return repairTruncatedJsonArguments(repairShellCommandArguments(repaired, toolName));
  }
  return JSON.stringify(value || {});
}

function repairShellCommandArguments(value, toolName) {
  if (!isShellCommandTool(toolName)) {
    return value;
  }
  try {
    JSON.parse(value);
    return value;
  } catch {
    // A quoted Windows directory ending in backslash can consume the JSON string's closing quote.
  }
  const propertyPattern = /,\s*"(?:timeout_ms|workdir|justification|sandbox_permissions|prefix_rule|login)"\s*:/g;
  const boundaries = [...value.matchAll(propertyPattern)].map((match) => match.index);
  for (const boundary of boundaries.reverse()) {
    const candidate = `${value.slice(0, boundary)}"${value.slice(boundary)}`;
    try {
      const parsed = JSON.parse(candidate);
      if (typeof parsed.command === 'string') {
        return candidate;
      }
    } catch {
      // Try the preceding property boundary.
    }
  }
  return value;
}

function isShellCommandTool(toolName) {
  return typeof toolName === 'string'
    && (toolName === 'shell_command' || toolName.endsWith('__shell_command'));
}

function sanitizeModelText(value) {
  return deduplicateModelText(value
    .replace(/<tool_call\b[^>]*>[\s\S]*?<\/tool_call>/gi, '')
    .replace(/<\/?(?:think|tool_call)\b[^>]*>/gi, ''));
}

function deduplicateModelText(value) {
  let result = value;
  while (result.length >= 80) {
    const marker = result.slice(0, Math.min(4, result.length));
    let boundary = result.indexOf(marker, Math.floor(result.length * 0.35));
    let duplicateBoundary = -1;
    while (boundary >= 0 && boundary <= result.length * 0.65) {
      const left = normalizeDuplicateText(result.slice(0, boundary));
      const right = normalizeDuplicateText(result.slice(boundary));
      if (left.length >= 24 && left === right) {
        duplicateBoundary = boundary;
        break;
      }
      boundary = result.indexOf(marker, boundary + marker.length);
    }
    if (duplicateBoundary < 0) {
      break;
    }
    result = result.slice(0, duplicateBoundary);
  }
  return result;
}

function normalizeDuplicateText(value) {
  return value.toLowerCase().replace(/[\p{P}\p{S}\s]+/gu, '');
}

function sessionKey(openAIRequest) {
  const messages = openAIRequest.messages || [];
  const system = messages
    .filter((message) => message.role === 'system')
    .map((message) => messageContentText(message.content))
    .filter((content) => !content.startsWith('[Gateway context compaction]'))
    .join('\n');
  const firstUser = messages.find((message) => message.role === 'user');
  return createHash('sha256')
    .update(JSON.stringify([system, firstUser?.content || '']))
    .digest('hex');
}
