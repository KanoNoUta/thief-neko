import { mapFinishReason, parseToolArguments } from './converters.js';

export function catpawStreamChunkToOpenAI(chunk) {
  if (!chunk?.statusCode) {
    return chunk;
  }

  const message = typeof chunk.msg === 'string' ? chunk.msg : JSON.stringify(chunk.msg ?? 'Unknown error');
  return {
    id: `catpaw_error_${Date.now()}`,
    choices: [{
      delta: { content: `[Catpaw ${chunk.statusCode}] ${message}` },
      finish_reason: 'stop',
    }],
  };
}

export function openAIStreamChunksToAnthropicEvents(chunks, model, options = {}) {
  const stream = new AnthropicStreamBuilder(model, options);

  for (const chunk of chunks) {
    stream.ingest(chunk);
  }

  return stream.finish();
}

export function parseOpenAISseChunk(text) {
  const chunks = [];

  for (const line of text.split(/\r?\n/)) {
    if (!line.startsWith('data:')) {
      continue;
    }

    const payload = line.slice(5).trim();
    if (!payload || payload === '[DONE]') {
      continue;
    }

    chunks.push(JSON.parse(payload));
  }

  return chunks;
}

export function formatSseEvent(event) {
  return `event: ${event.event}\ndata: ${JSON.stringify(event.data)}\n\n`;
}

export class AnthropicStreamBuilder {
  constructor(model, options = {}) {
    const maxBufferChars = options.maxBufferChars === undefined
      ? 4 * 1024 * 1024
      : options.maxBufferChars;
    if (!Number.isSafeInteger(maxBufferChars) || maxBufferChars <= 0) {
      throw new RangeError('maxBufferChars must be a positive safe integer');
    }

    this.model = model;
    this.collapseRepeatedText = Boolean(options.collapseRepeatedText);
    this.maxBufferChars = maxBufferChars;
    this.retainedChars = 0;
    this.usage = { input_tokens: 0, output_tokens: 0 };
    this.hasInputUsage = false;
    this.hasOutputUsage = false;
    this.textBuffer = '';
    this.events = [];
    this.started = false;
    this.textStarted = false;
    this.textStopped = false;
    this.nextIndex = 0;
    this.stopReason = null;
    this.toolCalls = new Map();
  }

  ingest(chunk) {
    this.updateUsage(chunk.usage);
    this.ensureStarted(chunk.id);
    const choice = chunk.choices?.[0] || {};
    const delta = choice.delta || {};

    if (delta.content) {
      if (this.collapseRepeatedText) {
        this.retain(delta.content.length);
        this.textBuffer += delta.content;
      } else {
        this.emitTextDelta(delta.content);
      }
    }

    if (Array.isArray(delta.tool_calls)) {
      this.collectToolCalls(delta.tool_calls);
    }

    const finishReason = choice.finish_reason || choice.finishReason;
    if (finishReason) {
      this.flushText();
      this.stopReason = mapFinishReason(finishReason);
      if (finishReason === 'tool_calls') {
        this.emitBufferedToolCalls();
      }
    }
  }

  finish() {
    this.ensureStarted();
    this.flushText();

    if (this.textStarted && !this.textStopped) {
      this.events.push({
        event: 'content_block_stop',
        data: { type: 'content_block_stop', index: 0 },
      });
      this.textStopped = true;
    }

    this.events.push({
      event: 'message_delta',
      data: {
        type: 'message_delta',
        delta: { stop_reason: this.stopReason || 'end_turn', stop_sequence: null },
        usage: { output_tokens: 0 },
      },
    });
    this.events.push({
      event: 'message_stop',
      data: { type: 'message_stop' },
    });

    this.events.at(-2).data.usage.output_tokens = this.usage.output_tokens;
    return this.events;
  }

  flushText() {
    if (!this.textBuffer) {
      return;
    }

    this.emitTextDelta(collapseExactRepeatedText(this.textBuffer), true);
    this.textBuffer = '';
  }

  ensureStarted(id = `msg_${Date.now()}`) {
    if (this.started) {
      return;
    }

    this.events.push({
      event: 'message_start',
      data: {
        type: 'message_start',
        message: {
          id,
          type: 'message',
          role: 'assistant',
          model: this.model,
          content: [],
          stop_reason: null,
          stop_sequence: null,
          usage: this.usage,
        },
      },
    });
    this.started = true;
  }

  emitTextDelta(text, alreadyRetained = false) {
    if (!alreadyRetained) {
      this.retain(text.length);
    }
    if (!this.textStarted) {
      this.events.push({
        event: 'content_block_start',
        data: {
          type: 'content_block_start',
          index: 0,
          content_block: { type: 'text', text: '' },
        },
      });
      this.textStarted = true;
    }

    this.events.push({
      event: 'content_block_delta',
      data: { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text } },
    });
  }

  collectToolCalls(toolCalls) {
    for (const toolCall of toolCalls) {
      const index = toolCall.index ?? 0;
      const current = this.toolCalls.get(index) || {
        id: '',
        name: '',
        arguments: '',
      };
      const previousLength = retainedToolCallLength(current);

      if (toolCall.id && current.id === toolCall.id) {
        current.name = toolCall.function?.name || current.name;
        current.arguments = toolCall.function?.arguments ?? current.arguments;
      } else {
        current.id += toolCall.id || '';
        current.name += toolCall.function?.name || '';
        current.arguments += toolCall.function?.arguments || '';
      }
      this.retain(retainedToolCallLength(current) - previousLength);
      this.toolCalls.set(index, current);
    }
  }

  updateUsage(usage) {
    if (!usage || typeof usage !== 'object') {
      return;
    }

    const inputTokens = usage.input_tokens ?? usage.prompt_tokens;
    const outputTokens = usage.output_tokens ?? usage.completion_tokens;
    if (isValidUsageValue(inputTokens)) {
      this.usage.input_tokens = inputTokens;
      this.hasInputUsage = true;
    }
    if (isValidUsageValue(outputTokens)) {
      this.usage.output_tokens = outputTokens;
      this.hasOutputUsage = true;
    }
  }

  retain(characterDelta) {
    const nextSize = this.retainedChars + characterDelta;
    if (nextSize > this.maxBufferChars) {
      const error = new RangeError(
        `Stream exceeded maximum of ${this.maxBufferChars} retained characters`,
      );
      error.code = 'CATAPI_STREAM_BUFFER_LIMIT';
      throw error;
    }
    this.retainedChars = Math.max(0, nextSize);
  }

  emitBufferedToolCalls() {
    if (this.textStarted && !this.textStopped) {
      this.events.push({
        event: 'content_block_stop',
        data: { type: 'content_block_stop', index: 0 },
      });
      this.textStopped = true;
    }

    for (const [offset, toolCall] of [...this.toolCalls.entries()].sort(([a], [b]) => a - b)) {
      const index = this.textStarted ? offset + 1 : offset;
      const inputJson = JSON.stringify(parseToolArguments(toolCall.arguments));
      this.events.push({
        event: 'content_block_start',
        data: {
          type: 'content_block_start',
          index,
          content_block: {
            type: 'tool_use',
            id: toolCall.id,
            name: toolCall.name,
            input: {},
          },
        },
      });
      this.events.push({
        event: 'content_block_delta',
        data: {
          type: 'content_block_delta',
          index,
          delta: { type: 'input_json_delta', partial_json: inputJson },
        },
      });
      this.events.push({
        event: 'content_block_stop',
        data: { type: 'content_block_stop', index },
      });
    }
  }
}

function retainedToolCallLength(toolCall) {
  return toolCall.id.length + toolCall.name.length + toolCall.arguments.length;
}

function isValidUsageValue(value) {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

function collapseExactRepeatedText(text) {
  for (let unitLength = 8; unitLength <= text.length / 2; unitLength += 1) {
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
