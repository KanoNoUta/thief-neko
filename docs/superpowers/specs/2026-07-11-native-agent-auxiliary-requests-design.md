# Native Agent Auxiliary Request Fix

## Problem

When `CATPAW_NATIVE_AGENT=1`, requests containing Claude tools use Catpaw's native Agent request envelope. Requests without a `tools` array instead use the plain OpenAI request shape. Claude Desktop emits these tool-free auxiliary requests during long sessions for work such as context maintenance. Catpaw rejects that protocol shape with `401 auth failed`, and Claude Desktop interprets the response as a rejected API key and signs the provider out.

The failure is not a rotated token: requests using the native Agent envelope succeed concurrently with the rejected auxiliary requests.

## Design

Treat `nativeAgent` as the protocol selection flag for every `/v1/messages` request. When it is enabled:

- Create or reuse a bounded Catpaw Agent session for the request.
- Build a Catpaw Agent envelope whether or not Claude supplied tools.
- Keep `use_mcp_tool` enabled with an empty `mcpTools` array for tool-free requests.
- Continue normalizing Catpaw Agent streaming responses through the existing Agent response path.

When `nativeAgent` is disabled, preserve the current plain OpenAI behavior.

## Alternatives

1. Retry a plain request after a 401 using the Agent envelope. Rejected because it consumes an unnecessary upstream attempt and can hide real authentication failures.
2. Special-case Claude auxiliary model names. Rejected because model names can change and the protocol requirement is independent of the requested model.

## Tests

Add an integration test that sends a streaming Anthropic request without tools while `nativeAgent` is enabled. The captured upstream request must contain the Catpaw Agent fields, use `glm-5.2` through the existing conversion, and return a valid Anthropic stream.

Keep the existing complete tool-loop test to ensure tool calls, conversation reuse, and `suggestUuid` mapping remain unchanged. Run the full Node test suite after the focused regression test passes.

## Success Criteria

- Tool-free auxiliary requests use the Catpaw Agent envelope.
- Tool-based requests retain their existing behavior.
- Native Agent disabled mode remains unchanged.
- The full test suite passes.
