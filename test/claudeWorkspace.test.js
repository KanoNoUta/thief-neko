import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import {
  mapWindowsPathToMount,
  resolveClaudeWorkspaceContext,
  rewriteClaudeFileToolCall,
} from '../src/claudeWorkspace.js';

test('resolveClaudeWorkspaceContext resolves one recent desktop session', async (t) => {
  const root = await createSessionRoot(t, [{
    sessionId: 'local-one',
    cliSessionId: 'cli-one',
    processName: 'session-one',
    userSelectedFolders: ['E:\\test1'],
    lastActivityAt: 9_000,
  }]);

  const context = await resolveClaudeWorkspaceContext({
    root,
    nowMs: 10_000,
    recentWindowMs: 5_000,
  });

  assert.deepEqual(context, {
    sessionId: 'local-one',
    cliSessionId: 'cli-one',
    processName: 'session-one',
    hostLoopMode: false,
    cwd: null,
    mappings: [{
      hostRoot: 'E:\\test1',
      mountRoot: '/sessions/session-one/mnt/test1',
    }],
  });
});

test('resolveClaudeWorkspaceContext prefers an explicit CLI session id', async (t) => {
  const root = await createSessionRoot(t, [
    {
      sessionId: 'local-one',
      cliSessionId: 'cli-one',
      processName: 'session-one',
      userSelectedFolders: ['E:\\one'],
      lastActivityAt: 9_000,
    },
    {
      sessionId: 'local-two',
      cliSessionId: 'cli-two',
      processName: 'session-two',
      userSelectedFolders: ['F:\\two'],
      lastActivityAt: 9_500,
    },
  ]);

  const context = await resolveClaudeWorkspaceContext({
    root,
    headers: { 'x-claude-session-id': 'cli-two' },
    nowMs: 10_000,
  });

  assert.equal(context.sessionId, 'local-two');
  assert.equal(context.mappings[0].mountRoot, '/sessions/session-two/mnt/two');
});

test('resolveClaudeWorkspaceContext returns null for ambiguous recent sessions', async (t) => {
  const root = await createSessionRoot(t, [
    {
      sessionId: 'local-one',
      processName: 'session-one',
      userSelectedFolders: ['E:\\one'],
      lastActivityAt: 9_000,
    },
    {
      sessionId: 'local-two',
      processName: 'session-two',
      userSelectedFolders: ['F:\\two'],
      lastActivityAt: 9_500,
    },
  ]);

  const context = await resolveClaudeWorkspaceContext({
    root,
    nowMs: 10_000,
    recentWindowMs: 5_000,
  });

  assert.equal(context, null);
});

test('resolveClaudeWorkspaceContext rejects duplicate mount basenames', async (t) => {
  const root = await createSessionRoot(t, [{
    sessionId: 'local-one',
    processName: 'session-one',
    userSelectedFolders: ['E:\\test1', 'F:\\test1'],
    lastActivityAt: 9_000,
  }]);

  const context = await resolveClaudeWorkspaceContext({
    root,
    nowMs: 10_000,
  });

  assert.equal(context, null);
});

test('resolveClaudeWorkspaceContext tolerates malformed unrelated metadata fields', async (t) => {
  const root = await createSessionRoot(t, []);
  const sessionsDir = join(root, 'account', '00000000');
  await writeFile(join(sessionsDir, 'local_malformed.json'), `{
    "sessionId": "local-host",
    "cliSessionId": "cli-host",
    "processName": "host-session",
    "cwd": "C:\\\\sessions\\\\outputs",
    "userSelectedFolders": ["E:\\\\test1"],
    "lastActivityAt": 9000,
    "hostLoopMode": true,
    "initialMessage": "broken string,
    "other": true
  }`);

  const context = await resolveClaudeWorkspaceContext({
    root,
    nowMs: 10_000,
    recentWindowMs: 5_000,
  });

  assert.equal(context.sessionId, 'local-host');
  assert.equal(context.hostLoopMode, true);
  assert.equal(context.cwd, 'C:\\sessions\\outputs');
  assert.equal(context.mappings[0].hostRoot, 'E:\\test1');
});

test('mapWindowsPathToMount maps only paths inside selected roots', () => {
  const context = workspaceContext();

  assert.equal(
    mapWindowsPathToMount('e:\\TEST1\\src\\app.js', context),
    '/sessions/session-one/mnt/test1/src/app.js',
  );
  assert.equal(mapWindowsPathToMount('E:\\outside\\app.js', context), null);
  assert.equal(mapWindowsPathToMount('E:\\test1\\..\\outside.txt', context), null);
  assert.equal(
    mapWindowsPathToMount('/sessions/session-one/mnt/test1/app.js', context),
    null,
  );
});

test('rewriteClaudeFileToolCall rewrites complete native file arguments', () => {
  const result = rewriteClaudeFileToolCall({
    id: 'call_1',
    type: 'function',
    function: {
      name: 'Write',
      arguments: '{"file_path":"E:\\\\test1\\\\note.txt","content":"ok"}',
    },
  }, workspaceContext());

  assert.deepEqual(JSON.parse(result.function.arguments), {
    file_path: '/sessions/session-one/mnt/test1/note.txt',
    content: 'ok',
  });
});

test('rewriteClaudeFileToolCall preserves Windows paths in host-loop sessions', () => {
  const toolCall = {
    id: 'call_1',
    type: 'function',
    function: {
      name: 'Write',
      arguments: '{"file_path":"E:\\\\test1\\\\note.txt","content":"ok"}',
    },
  };

  assert.deepEqual(
    rewriteClaudeFileToolCall(toolCall, { ...workspaceContext(), hostLoopMode: true }),
    toolCall,
  );
});

test('rewriteClaudeFileToolCall leaves partial JSON and Bash commands unchanged', () => {
  const partial = {
    function: { name: 'Read', arguments: '{"file_path":"E:\\\\test1' },
  };
  const bash = {
    function: {
      name: 'mcp__workspace__bash',
      arguments: '{"command":"cat E:\\\\test1\\\\note.txt"}',
    },
  };

  assert.deepEqual(rewriteClaudeFileToolCall(partial, workspaceContext()), partial);
  assert.deepEqual(rewriteClaudeFileToolCall(bash, workspaceContext()), bash);
});

async function createSessionRoot(t, sessions) {
  const root = await mkdtemp(join(tmpdir(), 'catapi-claude-sessions-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const sessionsDir = join(root, 'account', '00000000');
  await mkdir(sessionsDir, { recursive: true });

  await Promise.all(sessions.map((session, index) => writeFile(
    join(sessionsDir, `local_${index}.json`),
    JSON.stringify(session),
  )));
  return root;
}

function workspaceContext() {
  return {
    sessionId: 'local-one',
    cliSessionId: 'cli-one',
    processName: 'session-one',
    mappings: [{
      hostRoot: 'E:\\test1',
      mountRoot: '/sessions/session-one/mnt/test1',
    }],
  };
}
