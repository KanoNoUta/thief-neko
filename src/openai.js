export function normalizeOpenAIRequest(request, options = {}) {
  if (!request || typeof request !== 'object' || Array.isArray(request)) {
    throw badRequest('OpenAI request must be a JSON object');
  }
  if (!Array.isArray(request.messages)) {
    throw badRequest('messages must be an array');
  }
  if (request.tools !== undefined && !Array.isArray(request.tools)) {
    throw badRequest('tools must be an array');
  }

  return {
    ...request,
    model: options.model || request.model,
    stream: Boolean(request.stream),
    messages: request.messages.map((message) => ({ ...message })),
    ...(request.tools ? { tools: request.tools.map((tool) => ({ ...tool })) } : {}),
  };
}

export class OpenAIStreamAccumulator {
  constructor(model, options = {}) {
    this.model = model;
    this.collapseSnapshots = Boolean(options.collapseSnapshots);
    this.maxBufferChars = options.maxBufferChars ?? 4 * 1024 * 1024;
    if (!Number.isSafeInteger(this.maxBufferChars) || this.maxBufferChars <= 0) {
      throw new RangeError('maxBufferChars must be a positive safe integer');
    }
    this.id = '';
    this.created = Math.floor(Date.now() / 1000);
    this.content = '';
    this.toolCalls = new Map();
    this.finishReason = null;
    this.usage = null;
    this.hasInputUsage = false;
    this.hasOutputUsage = false;
    this.retainedChars = 0;
  }

  ingest(chunk) {
    this.id = chunk?.id || this.id || `chatcmpl_${Date.now()}`;
    this.captureUsage(chunk?.usage);
    const choice = chunk?.choices?.[0] || {};
    const sourceDelta = choice.delta || {};
    const delta = {};

    if (typeof sourceDelta.content === 'string' && sourceDelta.content) {
      const candidate = this.collapseSnapshots
        ? collapseExactRepeatedText(sourceDelta.content)
        : sourceDelta.content;
      const appended = appendFragment(this.content, candidate, this.collapseSnapshots);
      this.retain(appended.delta.length);
      this.content = appended.value;
      if (appended.delta) {
        delta.content = appended.delta;
      }
    }

    const toolDeltas = this.collectToolCalls(sourceDelta.tool_calls);
    if (toolDeltas.length > 0) {
      delta.tool_calls = toolDeltas;
    }

    const finishReason = choice.finish_reason || choice.finishReason || null;
    if (finishReason) {
      this.finishReason = finishReason;
    }

    return {
      id: this.id,
      object: 'chat.completion.chunk',
      created: this.created,
      model: this.model,
      choices: [{ index: 0, delta, finish_reason: finishReason }],
      ...(chunk?.usage ? { usage: this.usage } : {}),
    };
  }

  response() {
    const toolCalls = [...this.toolCalls.entries()]
      .sort(([left], [right]) => left - right)
      .map(([, toolCall]) => ({
        id: toolCall.id,
        type: toolCall.type || 'function',
        function: {
          name: toolCall.name,
          arguments: toolCall.arguments,
        },
      }));
    return {
      id: this.id || `chatcmpl_${Date.now()}`,
      object: 'chat.completion',
      created: this.created,
      model: this.model,
      choices: [{
        index: 0,
        message: {
          role: 'assistant',
          content: this.content || null,
          ...(toolCalls.length > 0 ? { tool_calls: toolCalls } : {}),
        },
        finish_reason: this.finishReason || (toolCalls.length > 0 ? 'tool_calls' : 'stop'),
      }],
      ...(this.usage ? { usage: this.usage } : {}),
    };
  }

  collectToolCalls(toolCalls) {
    if (!Array.isArray(toolCalls)) {
      return [];
    }
    const deltas = [];
    for (const incoming of toolCalls) {
      const index = incoming.index ?? 0;
      const current = this.toolCalls.get(index) || {
        id: '',
        type: incoming.type || 'function',
        name: '',
        arguments: '',
      };
      const next = { ...current };
      const id = appendFragment(current.id, incoming.id || '', this.collapseSnapshots);
      const name = appendFragment(
        current.name,
        incoming.function?.name || '',
        this.collapseSnapshots,
      );
      const rawArguments = incoming.function?.arguments;
      const argumentText = rawArguments === undefined
        ? ''
        : typeof rawArguments === 'string' ? rawArguments : JSON.stringify(rawArguments);
      const args = appendFragment(current.arguments, argumentText, this.collapseSnapshots);
      next.id = id.value;
      next.name = name.value;
      next.arguments = args.value;
      next.type = incoming.type || current.type;
      this.retain(
        id.value.length + name.value.length + args.value.length
        - current.id.length - current.name.length - current.arguments.length,
      );
      this.toolCalls.set(index, next);

      if (id.delta || name.delta || args.delta || !current.id) {
        deltas.push({
          index,
          ...(id.delta ? { id: id.delta } : {}),
          type: next.type,
          function: {
            ...(name.delta ? { name: name.delta } : {}),
            ...(args.delta ? { arguments: args.delta } : {}),
          },
        });
      }
    }
    return deltas;
  }

  captureUsage(usage) {
    if (!usage || typeof usage !== 'object') {
      return;
    }
    const promptTokens = validUsage(usage.prompt_tokens ?? usage.input_tokens);
    const completionTokens = validUsage(usage.completion_tokens ?? usage.output_tokens);
    if (promptTokens === null && completionTokens === null) {
      return;
    }
    const previous = this.usage || { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 };
    const prompt = promptTokens ?? previous.prompt_tokens;
    const completion = completionTokens ?? previous.completion_tokens;
    this.hasInputUsage ||= promptTokens !== null;
    this.hasOutputUsage ||= completionTokens !== null;
    this.usage = {
      prompt_tokens: prompt,
      completion_tokens: completion,
      total_tokens: prompt + completion,
    };
  }

  retain(delta) {
    this.retainedChars = Math.max(0, this.retainedChars + delta);
    if (this.retainedChars > this.maxBufferChars) {
      const error = new RangeError(
        `Stream exceeded maximum of ${this.maxBufferChars} retained characters`,
      );
      error.code = 'CATAPI_STREAM_BUFFER_LIMIT';
      throw error;
    }
  }
}

function appendFragment(current, incoming, collapseSnapshots) {
  if (!incoming) {
    return { value: current, delta: '' };
  }
  if (collapseSnapshots) {
    if (incoming === current || current.endsWith(incoming)) {
      return { value: current, delta: '' };
    }
    if (incoming.startsWith(current)) {
      return { value: incoming, delta: incoming.slice(current.length) };
    }
  }
  return { value: current + incoming, delta: incoming };
}

function collapseExactRepeatedText(text) {
  for (let unitLength = 1; unitLength <= text.length / 2; unitLength += 1) {
    if (text.length % unitLength !== 0) {
      continue;
    }
    const unit = text.slice(0, unitLength);
    if (unit.repeat(text.length / unitLength) === text) {
      return unit;
    }
  }
  return text;
}

function validUsage(value) {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
}

function badRequest(message) {
  const error = new TypeError(message);
  error.statusCode = 400;
  return error;
}
