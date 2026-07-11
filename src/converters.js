const TOOL_PROTOCOL_INSTRUCTION = 'Tool protocol: When an action is needed, call the provided OpenAI functions using tool_calls. Never print or describe pseudo tool directives, including fenced "start agents" blocks. Use the Agent function to start Explore or general-purpose agents. Do not claim a tool or agent was started unless you emitted a tool call. After tool results, continue calling tools until the user\'s task is complete.';
const WORKSPACE_FALLBACK_INSTRUCTION = 'Claude Desktop workspace paths: Native file tools run inside the local-agent VM. For selected folders, prefer their /sessions/<session>/mnt/<folder> mount paths. If an exact mapping is unavailable, inspect or use $PWD/mnt as the connected-folder fallback. Do not rewrite Bash command strings.';
const HOST_LOOP_WORKSPACE_INSTRUCTION = 'Claude Desktop workspace paths: Native Read/Write/Edit/Grep/Glob tools run on the Windows host. Use the exact selected Windows folder paths listed below. The /sessions/... mount paths are for mcp__workspace__bash only; never pass them to native file tools. Do not rewrite Bash command strings.';

export function anthropicToOpenAIRequest(request, options) {
  const messages = [];
  const hasTools = Array.isArray(request.tools) && request.tools.length > 0;
  const systemText = prepareSystemText(
    contentToText(request.system),
    options.maxSystemChars,
    hasTools,
    options.workspaceContext,
  );

  if (systemText) {
    messages.push({ role: 'system', content: systemText });
  }

  for (const message of request.messages || []) {
    messages.push(...anthropicMessageToOpenAI(message));
  }

  const body = {
    model: options.model || request.model,
    messages,
    max_tokens: request.max_tokens,
    stream: Boolean(request.stream),
  };

  if (request.temperature !== undefined) {
    body.temperature = request.temperature;
  }

  if (hasTools) {
    body.tools = request.tools.map((tool) => ({
      type: 'function',
      function: {
        name: tool.name,
        description: compactText(tool.description || '', options.maxToolDescriptionChars, ''),
        parameters: tool.input_schema || { type: 'object', properties: {} },
      },
    }));
    body.tool_choice = requestHasToolResult(request) ? 'auto' : 'required';
  }

  return body;
}

export function prepareOpenAIRequestForCatpaw(request, options = {}) {
  if (!Array.isArray(request.tools) || request.tools.length === 0) {
    return request;
  }
  const messages = (request.messages || []).map((message) => ({ ...message }));
  const systemIndex = messages.findIndex((message) => message.role === 'system');
  const existingSystem = systemIndex >= 0 ? contentToText(messages[systemIndex].content) : '';
  if (existingSystem.includes(TOOL_PROTOCOL_INSTRUCTION)) {
    return { ...request, messages };
  }
  const content = prepareSystemText(
    existingSystem,
    options.maxSystemChars,
    true,
    options.workspaceContext,
  );
  if (systemIndex >= 0) {
    messages[systemIndex] = { ...messages[systemIndex], content };
  } else {
    messages.unshift({ role: 'system', content });
  }
  return { ...request, messages };
}

function requestHasToolResult(request) {
  return (request.messages || []).some((message) => (
    Array.isArray(message.content)
    && message.content.some((block) => block?.type === 'tool_result')
  ));
}

function prepareSystemText(text, maxChars, hasTools, workspaceContext) {
  if (!hasTools) {
    return compactText(text, maxChars);
  }

  const instruction = buildToolProtocolInstruction(workspaceContext);
  const separator = text ? '\n\n' : '';
  if (!maxChars) {
    return `${text}${separator}${instruction}`;
  }

  const finalInstruction = instruction.length <= maxChars
    ? instruction
    : instruction.slice(0, maxChars);
  if (finalInstruction.length >= maxChars) {
    return finalInstruction;
  }

  const textBudget = Math.max(0, maxChars - finalInstruction.length - separator.length);
  return `${compactText(text, textBudget)}${separator}${finalInstruction}`;
}

function buildToolProtocolInstruction(workspaceContext) {
  const hostLoopMode = workspaceContext?.hostLoopMode === true;
  const lines = [
    TOOL_PROTOCOL_INSTRUCTION,
    hostLoopMode ? HOST_LOOP_WORKSPACE_INSTRUCTION : WORKSPACE_FALLBACK_INSTRUCTION,
  ];

  if (Array.isArray(workspaceContext?.mappings) && workspaceContext.mappings.length > 0) {
    lines.push(hostLoopMode ? 'Exact workspace paths:' : 'Exact workspace mounts:');
    for (const mapping of workspaceContext.mappings) {
      lines.push(hostLoopMode
        ? `Use this exact path with native file tools: ${mapping.hostRoot}; mcp__workspace__bash only: ${mapping.mountRoot}`
        : `${mapping.hostRoot} => ${mapping.mountRoot}`);
    }
  }

  return lines.join('\n');
}

function compactText(text, maxChars, marker = '\n\n[... host instructions compacted ...]\n\n') {
  if (!maxChars || text.length <= maxChars) {
    return text;
  }

  if (!marker) {
    return text.slice(0, maxChars);
  }

  const available = Math.max(0, maxChars - marker.length);
  const headChars = Math.ceil(available * 0.75);
  const tailChars = available - headChars;
  return `${text.slice(0, headChars)}${marker}${text.slice(text.length - tailChars)}`;
}

export function openAIToAnthropicMessage(response, model) {
  const choice = response.choices?.[0] || {};
  const message = choice.message || {};
  const content = [];

  if (message.content) {
    content.push({ type: 'text', text: String(message.content) });
  }

  for (const toolCall of message.tool_calls || []) {
    content.push({
      type: 'tool_use',
      id: toolCall.id,
      name: toolCall.function?.name || '',
      input: parseToolArguments(toolCall.function?.arguments),
    });
  }

  return {
    id: response.id || `msg_${Date.now()}`,
    type: 'message',
    role: 'assistant',
    model,
    content,
    stop_reason: mapFinishReason(choice.finish_reason),
    stop_sequence: null,
    usage: {
      input_tokens: response.usage?.prompt_tokens || 0,
      output_tokens: response.usage?.completion_tokens || 0,
    },
  };
}

export function normalizeOpenAIResponse(response) {
  if (response && typeof response === 'object' && 'status' in response && 'data' in response) {
    const status = Number(response.status);
    if (status !== 0 && status !== 200) {
      const message = response.data?.message || response.message || `Catpaw upstream returned status ${status}`;
      throw new Error(message);
    }

    if (response.data && typeof response.data === 'object') {
      return response.data;
    }
  }

  return response;
}

export function mapFinishReason(reason) {
  if (reason === 'length') {
    return 'max_tokens';
  }

  if (reason === 'tool_calls') {
    return 'tool_use';
  }

  return 'end_turn';
}

export function parseToolArguments(value) {
  if (!value) {
    return {};
  }

  if (typeof value === 'object') {
    return value;
  }

  try {
    return JSON.parse(value);
  } catch {
    return { raw: String(value) };
  }
}

function anthropicMessageToOpenAI(message) {
  const blocks = Array.isArray(message.content)
    ? message.content
    : [{ type: 'text', text: message.content || '' }];

  const toolResultMessages = blocks
    .filter((block) => block.type === 'tool_result')
    .map((block) => ({
      role: 'tool',
      tool_call_id: block.tool_use_id,
      content: contentToText(block.content),
    }));

  if (toolResultMessages.length > 0) {
    return toolResultMessages;
  }

  const text = blocks
    .filter((block) => block.type === 'text')
    .map((block) => block.text || '')
    .join('\n');
  const toolCalls = blocks
    .filter((block) => block.type === 'tool_use')
    .map((block) => ({
      id: block.id,
      type: 'function',
      function: {
        name: block.name,
        arguments: JSON.stringify(block.input || {}),
      },
    }));

  const mapped = {
    role: message.role,
    content: text,
  };

  if (toolCalls.length > 0) {
    mapped.tool_calls = toolCalls;
  }

  return [mapped];
}

function contentToText(content) {
  if (!content) {
    return '';
  }

  if (typeof content === 'string') {
    return content;
  }

  if (Array.isArray(content)) {
    return content
      .map((block) => {
        if (typeof block === 'string') {
          return block;
        }
        if (block.type === 'text') {
          return block.text || '';
        }
        if ('content' in block) {
          return contentToText(block.content);
        }
        return '';
      })
      .filter(Boolean)
      .join('\n');
  }

  return String(content);
}
