import { randomUUID } from 'node:crypto';

const STATEFUL_FIELDS = ['conversation', 'prompt', 'context_management'];
const HOSTED_TOOL_TYPES = new Set([
  'code_interpreter',
  'computer_use_preview',
  'file_search',
  'image_generation',
  'mcp',
  'web_search',
  'web_search_preview',
]);
const DEFAULT_MAX_HISTORY_CHARS = 192 * 1024;
const COMPACTION_NOTICE = '[Gateway context compaction] Earlier tool-loop messages were removed. '
  + 'Use the current workspace state and recent tool results as the source of truth.';

export function responsesToOpenAIRequest(request, options = {}) {
  if (!request || typeof request !== 'object' || Array.isArray(request)) {
    throw badRequest('Responses request must be a JSON object');
  }
  for (const field of STATEFUL_FIELDS) {
    if (request[field] !== undefined && request[field] !== null && request[field] !== '') {
      throw badRequest(`${field} is not supported by the Catpaw compatibility gateway`);
    }
  }

  const messages = [];
  if (request.instructions) {
    messages.push({ role: 'system', content: contentText(request.instructions) });
  }
  if (typeof request.input === 'string') {
    messages.push({ role: 'user', content: request.input });
  } else if (Array.isArray(request.input)) {
    for (const item of request.input) {
      appendResponsesInput(messages, item);
    }
  } else if (request.input !== undefined) {
    throw badRequest('input must be a string or an array');
  }

  const result = {
    model: options.model || request.model,
    messages,
    stream: Boolean(request.stream),
  };
  copyDefined(result, request, [
    'temperature',
    'top_p',
    'parallel_tool_calls',
    'user',
    'metadata',
  ]);
  if (request.max_output_tokens !== undefined) {
    result.max_tokens = request.max_output_tokens;
  }
  if (request.reasoning?.effort) {
    result.reasoning_effort = request.reasoning.effort;
  }
  const toolMetadata = options.toolMetadata || responsesToolMetadata(request);
  if (Array.isArray(request.tools)) {
    result.tools = toolMetadata.openAITools;
  }
  if (request.tool_choice !== undefined) {
    result.tool_choice = responsesToolChoiceToOpenAI(
      request.tool_choice,
      toolMetadata.namespaceTools,
    );
  }
  return result;
}

export function responsesCustomToolNames(request) {
  return responsesToolMetadata(request).customToolNames;
}

export function responsesKnownAgentIds(request) {
  const ids = new Set();
  for (const message of request?.messages || []) {
    if (message?.role !== 'tool' || typeof message.content !== 'string') {
      continue;
    }
    for (const match of message.content.matchAll(/"agent_id"\s*:\s*"([0-9a-f-]{36})"/gi)) {
      ids.add(match[1].toLowerCase());
    }
  }
  return ids;
}

export function responsesMalformedToolResultCount(request) {
  let count = 0;
  const messages = request?.messages || [];
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const message = messages[index];
    if (message?.role === 'assistant' && Array.isArray(message.tool_calls)) {
      continue;
    }
    if (
      message?.role === 'tool'
      && typeof message.content === 'string'
      && /failed to parse function arguments:\s*missing field/i.test(message.content)
    ) {
      count += 1;
      continue;
    }
    break;
  }
  return count;
}

export function responsesToolMetadata(request) {
  const tools = Array.isArray(request?.tools) ? request.tools : [];
  const plainToolNames = new Set(tools
    .filter((tool) => tool?.type !== 'namespace' && tool?.name)
    .map((tool) => tool.name));
  const usedNames = new Set(plainToolNames);
  const customToolNames = new Set();
  const namespaceTools = new Map();
  const openAITools = [];

  for (const tool of tools) {
    if (tool?.type !== 'namespace') {
      if (HOSTED_TOOL_TYPES.has(tool?.type)) {
        continue;
      }
      openAITools.push(responsesToolToOpenAI(tool));
      if (tool?.type === 'custom') {
        customToolNames.add(tool.name);
      }
      continue;
    }
    if (!tool.name || !Array.isArray(tool.tools)) {
      throw badRequest('namespace tools require a name and tools array');
    }
    for (const child of tool.tools) {
      if (!child?.name) {
        throw badRequest('every namespace child tool must have a name');
      }
      if (child.type !== 'function' && child.type !== 'custom') {
        throw badRequest(`namespace child tool type ${child.type || 'unknown'} is not supported`);
      }
      const alias = namespacedToolAlias(tool.name, child.name, usedNames);
      const description = [
        `Namespace: ${tool.name}.`,
        tool.description || '',
        child.description || '',
      ].filter(Boolean).join(' ');
      openAITools.push(responsesToolToOpenAI({ ...child, name: alias, description }));
      namespaceTools.set(alias, {
        namespace: tool.name,
        name: child.name,
        type: child.type,
        allowBareName: false,
      });
      if (child.type === 'custom') {
        customToolNames.add(alias);
      }
    }
  }
  const childNameCounts = new Map();
  for (const descriptor of namespaceTools.values()) {
    childNameCounts.set(descriptor.name, (childNameCounts.get(descriptor.name) || 0) + 1);
  }
  for (const descriptor of namespaceTools.values()) {
    descriptor.allowBareName = !plainToolNames.has(descriptor.name)
      && childNameCounts.get(descriptor.name) === 1;
  }
  return { openAITools, customToolNames, namespaceTools };
}

export function createResponseId() {
  return responsesId();
}

export class ResponsesSessionStore {
  constructor({
    maxSessions = 128,
    ttlMs = 6 * 60 * 60 * 1000,
    maxSessionChars = 16 * 1024 * 1024,
    maxTotalChars = 64 * 1024 * 1024,
    maxHistoryChars,
    now = Date.now,
  } = {}) {
    if (!Number.isSafeInteger(maxSessions) || maxSessions <= 0) {
      throw new RangeError('maxSessions must be a positive safe integer');
    }
    if (!Number.isSafeInteger(ttlMs) || ttlMs <= 0) {
      throw new RangeError('ttlMs must be a positive safe integer');
    }
    if (!Number.isSafeInteger(maxSessionChars) || maxSessionChars <= 0) {
      throw new RangeError('maxSessionChars must be a positive safe integer');
    }
    if (!Number.isSafeInteger(maxTotalChars) || maxTotalChars < maxSessionChars) {
      throw new RangeError('maxTotalChars must be a safe integer at least maxSessionChars');
    }
    const historyLimit = maxHistoryChars ?? Math.min(DEFAULT_MAX_HISTORY_CHARS, maxSessionChars);
    if (!Number.isSafeInteger(historyLimit) || historyLimit <= 0 || historyLimit > maxSessionChars) {
      throw new RangeError('maxHistoryChars must be a positive safe integer at most maxSessionChars');
    }
    if (typeof now !== 'function') {
      throw new TypeError('now must be a function');
    }
    this.maxSessions = maxSessions;
    this.ttlMs = ttlMs;
    this.maxSessionChars = maxSessionChars;
    this.maxTotalChars = maxTotalChars;
    this.maxHistoryChars = historyLimit;
    this.now = now;
    this.sessions = new Map();
    this.totalChars = 0;
  }

  record(
    responseId,
    request,
    response,
    customToolNames = new Set(),
    namespaceTools = new Map(),
    knownAgentIds = new Set(),
  ) {
    const assistant = response?.choices?.[0]?.message;
    const messages = structuredClone(request?.messages || []);
    if (assistant) {
      messages.push(structuredClone(assistant));
    }
    const originalChars = JSON.stringify(messages).length;
    this.sweep();
    this.delete(responseId);
    if (originalChars > this.maxSessionChars) {
      return false;
    }
    const retainedMessages = compactResponseHistory(messages, this.maxHistoryChars);
    const retainedChars = JSON.stringify(retainedMessages).length;
    while (this.sessions.size > 0 && this.totalChars + retainedChars > this.maxTotalChars) {
      this.delete(this.sessions.keys().next().value);
    }
    this.sessions.set(responseId, {
      messages: retainedMessages,
      customToolNames: new Set(customToolNames),
      namespaceTools: new Map(namespaceTools),
      knownAgentIds: new Set([
        ...knownAgentIds,
        ...responsesKnownAgentIds({ messages }),
      ]),
      lastAccessAt: this.now(),
      retainedChars,
    });
    this.totalChars += retainedChars;
    while (this.sessions.size > this.maxSessions) {
      this.delete(this.sessions.keys().next().value);
    }
    return true;
  }

  resume(previousResponseId, request) {
    this.sweep();
    const session = this.sessions.get(previousResponseId);
    if (!session) {
      const error = badRequest('previous response was not found or has expired');
      error.code = 'previous_response_not_found';
      throw error;
    }
    session.lastAccessAt = this.now();
    this.sessions.delete(previousResponseId);
    this.sessions.set(previousResponseId, session);
    return {
      request: {
        ...request,
        messages: [
          ...structuredClone(session.messages),
          ...structuredClone(request?.messages || []),
        ],
      },
      customToolNames: new Set(session.customToolNames),
      namespaceTools: new Map(session.namespaceTools || []),
      knownAgentIds: new Set(session.knownAgentIds || []),
    };
  }

  sweep() {
    const expiresAt = this.now() - this.ttlMs;
    for (const [id, session] of this.sessions) {
      if (session.lastAccessAt <= expiresAt) {
        this.delete(id);
      }
    }
  }

  delete(id) {
    const session = this.sessions.get(id);
    if (!session) {
      return false;
    }
    this.totalChars = Math.max(0, this.totalChars - session.retainedChars);
    return this.sessions.delete(id);
  }
}

function compactResponseHistory(messages, maxChars) {
  if (JSON.stringify(messages).length <= maxChars) {
    return messages;
  }
  const anchorIndexes = new Set();
  let firstTaskIndex = -1;
  for (let index = 0; index < messages.length; index += 1) {
    if (messages[index]?.role === 'system') {
      anchorIndexes.add(index);
    } else if (firstTaskIndex < 0 && messages[index]?.role === 'user') {
      firstTaskIndex = index;
      anchorIndexes.add(index);
    }
  }
  const anchors = messages.filter((_, index) => anchorIndexes.has(index));
  const notice = { role: 'system', content: COMPACTION_NOTICE };
  const fixed = [...anchors, notice];
  let retainedChars = JSON.stringify(fixed).length;
  const units = historyUnits(messages, anchorIndexes);
  const recent = [];
  for (let index = units.length - 1; index >= 0; index -= 1) {
    const unitChars = JSON.stringify(units[index]).length;
    if (retainedChars + unitChars > maxChars) {
      continue;
    }
    recent.unshift(...units[index]);
    retainedChars += unitChars;
  }
  return [...anchors, notice, ...recent];
}

function historyUnits(messages, excludedIndexes) {
  const units = [];
  for (let index = 0; index < messages.length; index += 1) {
    if (excludedIndexes.has(index)) {
      continue;
    }
    const message = messages[index];
    if (Array.isArray(message?.tool_calls) && message.tool_calls.length > 0) {
      const callIds = new Set(message.tool_calls.map((call) => call?.id).filter(Boolean));
      const unit = [message];
      while (
        index + 1 < messages.length
        && messages[index + 1]?.role === 'tool'
        && callIds.has(messages[index + 1]?.tool_call_id)
      ) {
        index += 1;
        unit.push(messages[index]);
      }
      units.push(unit);
    } else {
      units.push([message]);
    }
  }
  return units;
}

export function openAIResponseToResponses(openAIResponse, options = {}) {
  const choice = openAIResponse?.choices?.[0] || {};
  const message = choice.message || {};
  const responseId = options.responseId || responsesId();
  const customToolNames = options.customToolNames || new Set();
  const namespaceTools = options.namespaceTools || new Map();
  const knownAgentIds = options.knownAgentIds || new Set();
  const output = [];
  if (message.content) {
    output.push(messageOutput(responseId, String(message.content), 'completed'));
  }
  for (const toolCall of message.tool_calls || []) {
    output.push(toolOutput(
      toolCall,
      customToolNames,
      namespaceTools,
      'completed',
      knownAgentIds,
    ));
  }
  return responseObject({
    id: responseId,
    model: openAIResponse.model || options.model || '',
    status: 'completed',
    output,
    usage: responsesUsage(openAIResponse.usage),
    createdAt: openAIResponse.created,
  });
}

export class ResponsesStreamBuilder {
  constructor(model, options = {}) {
    this.model = model;
    this.responseId = options.responseId || responsesId();
    this.customToolNames = options.customToolNames || new Set();
    this.namespaceTools = options.namespaceTools || new Map();
    this.knownAgentIds = options.knownAgentIds || new Set();
    this.createdAt = Math.floor(Date.now() / 1000);
    this.sequence = 0;
    this.started = false;
    this.finished = false;
    this.text = '';
    this.textOutputIndex = -1;
    this.tools = new Map();
    this.nextOutputIndex = 0;
    this.finishReason = null;
    this.usage = null;
  }

  ingest(chunk) {
    if (this.finished) {
      return [];
    }
    const events = this.ensureStarted();
    const choice = chunk?.choices?.[0] || {};
    const delta = choice.delta || {};
    if (typeof delta.content === 'string' && delta.content) {
      events.push(...this.appendText(delta.content));
    }
    for (const toolCall of delta.tool_calls || []) {
      events.push(...this.appendTool(toolCall));
    }
    this.finishReason = choice.finish_reason || choice.finishReason || this.finishReason;
    this.usage = responsesUsage(chunk?.usage) || this.usage;
    return events;
  }

  finish() {
    if (this.finished) {
      return [];
    }
    const events = this.ensureStarted();
    const output = [];
    if (this.textOutputIndex >= 0) {
      const item = messageOutput(this.responseId, this.text, 'completed');
      events.push(this.event('response.output_text.done', {
        item_id: item.id,
        output_index: this.textOutputIndex,
        content_index: 0,
        text: this.text,
      }));
      events.push(this.event('response.content_part.done', {
        item_id: item.id,
        output_index: this.textOutputIndex,
        content_index: 0,
        part: item.content[0],
      }));
      events.push(this.event('response.output_item.done', {
        output_index: this.textOutputIndex,
        item,
      }));
      output[this.textOutputIndex] = item;
    }
    for (const [, tool] of [...this.tools.entries()].sort(([left], [right]) => left - right)) {
      const item = completedToolOutput(
        tool,
        this.customToolNames,
        this.namespaceTools,
        'completed',
        this.knownAgentIds,
      );
      const custom = item.type === 'custom_tool_call';
      const value = custom ? item.input : item.arguments;
      events.push(this.event(
        custom
          ? 'response.custom_tool_call_input.delta'
          : 'response.function_call_arguments.done',
        {
          item_id: item.id,
          output_index: tool.outputIndex,
          ...(custom ? { delta: value } : { arguments: value }),
        },
      ));
      if (custom) {
        events.push(this.event('response.custom_tool_call_input.done', {
          item_id: item.id,
          output_index: tool.outputIndex,
          input: value,
        }));
      }
      events.push(this.event('response.output_item.done', {
        output_index: tool.outputIndex,
        item,
      }));
      output[tool.outputIndex] = item;
    }
    const response = responseObject({
      id: this.responseId,
      model: this.model,
      status: 'completed',
      output: output.filter(Boolean),
      usage: this.usage,
      createdAt: this.createdAt,
    });
    events.push(this.event('response.completed', { response }));
    this.finished = true;
    return events;
  }

  ensureStarted() {
    if (this.started) {
      return [];
    }
    this.started = true;
    const response = responseObject({
      id: this.responseId,
      model: this.model,
      status: 'in_progress',
      output: [],
      usage: null,
      createdAt: this.createdAt,
    });
    return [
      this.event('response.created', { response }),
      this.event('response.in_progress', { response }),
    ];
  }

  appendText(delta) {
    const events = [];
    if (this.textOutputIndex < 0) {
      this.textOutputIndex = this.nextOutputIndex++;
      const item = messageOutput(this.responseId, '', 'in_progress');
      events.push(this.event('response.output_item.added', {
        output_index: this.textOutputIndex,
        item,
      }));
      events.push(this.event('response.content_part.added', {
        item_id: item.id,
        output_index: this.textOutputIndex,
        content_index: 0,
        part: item.content[0],
      }));
    }
    this.text += delta;
    events.push(this.event('response.output_text.delta', {
      item_id: messageId(this.responseId),
      output_index: this.textOutputIndex,
      content_index: 0,
      delta,
    }));
    return events;
  }

  appendTool(toolCall) {
    const index = toolCall.index ?? 0;
    let tool = this.tools.get(index);
    const events = [];
    if (!tool) {
      tool = {
        outputIndex: this.nextOutputIndex++,
        id: toolCall.id || `call_${randomUUID()}`,
        name: toolCall.function?.name || '',
        arguments: '',
      };
      this.tools.set(index, tool);
      events.push(this.event('response.output_item.added', {
        output_index: tool.outputIndex,
        item: pendingToolOutput(tool, this.customToolNames, this.namespaceTools),
      }));
    }
    if (toolCall.id) {
      tool.id += tool.id ? appendOnlyNew(tool.id, toolCall.id) : toolCall.id;
    }
    if (toolCall.function?.name) {
      tool.name += appendOnlyNew(tool.name, toolCall.function.name);
    }
    const args = toolCall.function?.arguments || '';
    const argumentDelta = appendOnlyNew(tool.arguments, args);
    tool.arguments += argumentDelta;
    if (argumentDelta && !this.customToolNames.has(tool.name)) {
      events.push(this.event('response.function_call_arguments.delta', {
        item_id: functionItemId(tool.id),
        output_index: tool.outputIndex,
        delta: argumentDelta,
      }));
    }
    return events;
  }

  event(type, fields) {
    return { type, sequence_number: this.sequence++, ...fields };
  }
}

function appendResponsesInput(messages, item) {
  if (!item || typeof item !== 'object') {
    return;
  }
  if (item.type === 'function_call' || item.type === 'custom_tool_call') {
    const argumentsValue = item.type === 'custom_tool_call'
      ? JSON.stringify({ input: contentText(item.input) })
      : argumentString(item.arguments);
    messages.push({
      role: 'assistant',
      content: null,
      tool_calls: [{
        id: item.call_id || item.id,
        type: 'function',
        function: { name: item.name, arguments: argumentsValue },
      }],
    });
    return;
  }
  if (item.type === 'function_call_output' || item.type === 'custom_tool_call_output') {
    messages.push({
      role: 'tool',
      tool_call_id: item.call_id,
      content: contentText(item.output),
    });
    return;
  }
  if (item.type === 'message' || item.role) {
    messages.push({
      role: item.role === 'developer' ? 'system' : item.role,
      content: responsesContentToChat(item.content),
    });
  }
}

function responsesContentToChat(content) {
  if (typeof content === 'string') {
    return content;
  }
  if (!Array.isArray(content)) {
    return contentText(content);
  }
  const parts = content.map((part) => {
    if (typeof part === 'string') {
      return { type: 'text', text: part };
    }
    if (['input_text', 'output_text', 'text'].includes(part?.type)) {
      return { type: 'text', text: part.text || '' };
    }
    if (part?.type === 'input_image') {
      return {
        type: 'image_url',
        image_url: { url: part.image_url, ...(part.detail ? { detail: part.detail } : {}) },
      };
    }
    return { type: 'text', text: contentText(part) };
  });
  return parts.every((part) => part.type === 'text')
    ? parts.map((part) => part.text).join('')
    : parts;
}

function responsesToolToOpenAI(tool) {
  if (!tool?.name) {
    throw badRequest('every tool must have a name');
  }
  if (tool.type === 'function') {
    return {
      type: 'function',
      function: {
        name: tool.name,
        description: tool.description || '',
        parameters: tool.parameters || { type: 'object', properties: {} },
        ...(tool.strict !== undefined ? { strict: tool.strict } : {}),
      },
    };
  }
  if (tool.type === 'custom') {
    return {
      type: 'function',
      function: {
        name: tool.name,
        description: tool.description || '',
        parameters: {
          type: 'object',
          properties: { input: { type: 'string' } },
          required: ['input'],
          additionalProperties: false,
        },
      },
    };
  }
  throw badRequest(`tool type ${tool.type || 'unknown'} is not supported`);
}

function responsesToolChoiceToOpenAI(choice, namespaceTools = new Map()) {
  if (typeof choice === 'string') {
    return choice;
  }
  if (choice?.type === 'function' || choice?.type === 'custom') {
    const alias = choice.namespace
      ? findNamespacedToolAlias(namespaceTools, choice.namespace, choice.name)
      : choice.name;
    return { type: 'function', function: { name: alias } };
  }
  if (choice?.type === 'namespace') {
    return 'required';
  }
  return choice;
}

function messageOutput(responseId, text, status) {
  return {
    id: messageId(responseId),
    type: 'message',
    status,
    role: 'assistant',
    content: [{ type: 'output_text', text, annotations: [] }],
  };
}

function toolOutput(toolCall, customToolNames, namespaceTools, status, knownAgentIds = new Set()) {
  const tool = {
    id: toolCall.id,
    name: toolCall.function?.name || '',
    arguments: argumentString(toolCall.function?.arguments),
  };
  return completedToolOutput(tool, customToolNames, namespaceTools, status, knownAgentIds);
}

function pendingToolOutput(tool, customToolNames, namespaceTools = new Map()) {
  const custom = customToolNames.has(tool.name);
  const descriptor = namespaceToolDescriptor(namespaceTools, tool.name);
  const wireName = descriptor?.name || tool.name;
  const namespace = descriptor?.namespace;
  return custom
    ? {
        id: functionItemId(tool.id),
        type: 'custom_tool_call',
        status: 'in_progress',
        call_id: tool.id,
        name: wireName,
        ...(namespace ? { namespace } : {}),
        input: '',
      }
    : {
        id: functionItemId(tool.id),
        type: 'function_call',
        status: 'in_progress',
        call_id: tool.id,
        name: wireName,
        ...(namespace ? { namespace } : {}),
        arguments: '',
      };
}

function namespaceToolDescriptor(namespaceTools, name) {
  const exact = namespaceTools.get(name);
  if (exact) {
    return exact;
  }
  for (const descriptor of namespaceTools.values()) {
    if (descriptor.allowBareName && descriptor.name === name) {
      return descriptor;
    }
  }
  return undefined;
}

function completedToolOutput(
  tool,
  customToolNames,
  namespaceTools = new Map(),
  status = 'completed',
  knownAgentIds = new Set(),
) {
  const pending = pendingToolOutput(tool, customToolNames, namespaceTools);
  if (pending.type === 'custom_tool_call') {
    return { ...pending, status, input: customInput(tool.arguments) };
  }
  return {
    ...pending,
    status,
    arguments: repairAgentTarget(pending, tool.arguments, knownAgentIds),
  };
}

function repairAgentTarget(tool, argumentText, knownAgentIds) {
  if (tool.namespace !== 'multi_agent_v1' || tool.name !== 'close_agent') {
    return argumentText;
  }
  let parsed;
  try {
    parsed = JSON.parse(argumentText);
  } catch {
    return argumentText;
  }
  if (typeof parsed?.target !== 'string' || knownAgentIds.has(parsed.target.toLowerCase())) {
    return argumentText;
  }
  const shortened = parsed.target.toLowerCase();
  const matches = [...knownAgentIds].filter((candidate) => (
    candidate.length === shortened.length + 1
    && [...candidate].some((_, index) => (
      candidate.slice(0, index) + candidate.slice(index + 1) === shortened
    ))
  ));
  if (matches.length !== 1) {
    return argumentText;
  }
  return JSON.stringify({ ...parsed, target: matches[0] });
}

function namespacedToolAlias(namespace, name, usedNames) {
  const normalized = `${namespace}__${name}`.replace(/[^a-zA-Z0-9_-]/g, '_');
  let suffix = '';
  let counter = 1;
  while (true) {
    const maxBaseLength = Math.max(1, 64 - suffix.length);
    const candidate = `${normalized.slice(0, maxBaseLength)}${suffix}`;
    if (!usedNames.has(candidate)) {
      usedNames.add(candidate);
      return candidate;
    }
    counter += 1;
    suffix = `_${counter}`;
  }
}

function findNamespacedToolAlias(namespaceTools, namespace, name) {
  for (const [alias, descriptor] of namespaceTools) {
    if (descriptor.namespace === namespace && descriptor.name === name) {
      return alias;
    }
  }
  throw badRequest(`tool ${namespace}.${name} was not found`);
}

function responseObject({ id, model, status, output, usage, createdAt }) {
  return {
    id,
    object: 'response',
    created_at: createdAt || Math.floor(Date.now() / 1000),
    status,
    error: null,
    incomplete_details: null,
    model,
    output,
    parallel_tool_calls: true,
    usage,
  };
}

function responsesUsage(usage) {
  if (!usage) {
    return null;
  }
  const inputTokens = usage.input_tokens ?? usage.prompt_tokens ?? 0;
  const outputTokens = usage.output_tokens ?? usage.completion_tokens ?? 0;
  return {
    input_tokens: inputTokens,
    output_tokens: outputTokens,
    total_tokens: usage.total_tokens ?? inputTokens + outputTokens,
  };
}

function customInput(argumentsValue) {
  try {
    const parsed = JSON.parse(argumentsValue || '{}');
    return typeof parsed?.input === 'string' ? parsed.input : argumentsValue;
  } catch {
    return argumentsValue;
  }
}

function argumentString(value) {
  return typeof value === 'string' ? value : JSON.stringify(value || {});
}

function contentText(value) {
  if (value === null || value === undefined) {
    return '';
  }
  if (typeof value === 'string') {
    return value;
  }
  return JSON.stringify(value);
}

function appendOnlyNew(current, incoming) {
  if (!incoming || incoming === current || current.endsWith(incoming)) {
    return '';
  }
  return incoming.startsWith(current) ? incoming.slice(current.length) : incoming;
}

function copyDefined(target, source, names) {
  for (const name of names) {
    if (source[name] !== undefined) {
      target[name] = source[name];
    }
  }
}

function responsesId() {
  return `resp_${randomUUID().replaceAll('-', '')}`;
}

function messageId(responseId) {
  return `msg_${responseId.replace(/^resp_/, '')}`;
}

function functionItemId(callId) {
  return `fc_${String(callId || randomUUID()).replaceAll('-', '')}`;
}

function badRequest(message) {
  const error = new TypeError(message);
  error.statusCode = 400;
  return error;
}
