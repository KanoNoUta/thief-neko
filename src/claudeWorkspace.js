import { readdir, readFile, stat } from 'node:fs/promises';
import { posix, win32 } from 'node:path';

const SESSION_HEADER_NAMES = [
  'x-claude-session-id',
  'x-claude-code-session-id',
  'anthropic-session-id',
  'x-session-id',
];
const SESSION_METADATA_NAMES = [
  'session_id',
  'sessionId',
  'claude_session_id',
  'claudeSessionId',
];
const SKIPPED_DIRECTORIES = new Set([
  'spaces',
  'skills-plugin',
  'claude-code-vm',
]);
const FILE_TOOL_PATH_FIELDS = new Map([
  ['Read', ['file_path']],
  ['Write', ['file_path']],
  ['Edit', ['file_path']],
  ['Glob', ['path']],
  ['Grep', ['path']],
]);

export async function resolveClaudeWorkspaceContext({
  root,
  headers = {},
  metadata = {},
  nowMs = Date.now(),
  recentWindowMs = 5 * 60 * 1000,
} = {}) {
  if (!root) {
    return null;
  }

  const sessionFiles = await findSessionFiles(root);
  const sessions = (await Promise.all(sessionFiles.map(readSessionMetadata))).filter(Boolean);
  const explicitSessionId = findExplicitSessionId(headers, metadata);
  let candidates;

  if (explicitSessionId) {
    candidates = sessions.filter((session) => (
      session.sessionId === explicitSessionId
      || session.cliSessionId === explicitSessionId
    ));
  } else {
    candidates = sessions.filter((session) => (
      nowMs - session.activityAt >= 0
      && nowMs - session.activityAt <= recentWindowMs
    ));
  }

  if (candidates.length !== 1) {
    return null;
  }

  return buildWorkspaceContext(candidates[0]);
}

export function mapWindowsPathToMount(value, context) {
  if (typeof value !== 'string' || !win32.isAbsolute(value) || !context?.mappings?.length) {
    return null;
  }

  const normalizedValue = trimWindowsSeparator(win32.normalize(value));
  const mappings = [...context.mappings].sort(
    (left, right) => right.hostRoot.length - left.hostRoot.length,
  );

  for (const mapping of mappings) {
    const hostRoot = trimWindowsSeparator(win32.normalize(mapping.hostRoot));
    const relativePath = win32.relative(hostRoot, normalizedValue);
    if (
      relativePath === '..'
      || relativePath.startsWith(`..${win32.sep}`)
      || win32.isAbsolute(relativePath)
    ) {
      continue;
    }

    const segments = relativePath ? relativePath.split(win32.sep).filter(Boolean) : [];
    return posix.join(mapping.mountRoot, ...segments);
  }

  return null;
}

export function rewriteClaudeFileToolCall(toolCall, context) {
  if (context?.hostLoopMode) {
    return toolCall;
  }

  const name = toolCall?.function?.name;
  const pathFields = FILE_TOOL_PATH_FIELDS.get(name);
  const argumentText = toolCall?.function?.arguments;
  if (!pathFields || typeof argumentText !== 'string') {
    return toolCall;
  }

  let args;
  try {
    args = JSON.parse(argumentText);
  } catch {
    return toolCall;
  }
  if (!args || typeof args !== 'object' || Array.isArray(args)) {
    return toolCall;
  }

  let changed = false;
  const rewrittenArgs = { ...args };
  for (const field of pathFields) {
    const mappedPath = mapWindowsPathToMount(args[field], context);
    if (mappedPath) {
      rewrittenArgs[field] = mappedPath;
      changed = true;
    }
  }

  if (!changed) {
    return toolCall;
  }

  return {
    ...toolCall,
    function: {
      ...toolCall.function,
      arguments: JSON.stringify(rewrittenArgs),
    },
  };
}

async function findSessionFiles(root, depth = 0) {
  if (depth > 3) {
    return [];
  }

  let entries;
  try {
    entries = await readdir(root, { withFileTypes: true });
  } catch {
    return [];
  }

  const files = [];
  for (const entry of entries) {
    const entryPath = posixOrWindowsJoin(root, entry.name);
    if (entry.isFile() && /^local_.+\.json$/i.test(entry.name)) {
      files.push(entryPath);
      continue;
    }
    if (
      entry.isDirectory()
      && !entry.name.startsWith('local_')
      && !SKIPPED_DIRECTORIES.has(entry.name)
    ) {
      files.push(...await findSessionFiles(entryPath, depth + 1));
    }
  }
  return files;
}

async function readSessionMetadata(filePath) {
  try {
    const [raw, fileStat] = await Promise.all([
      readFile(filePath, 'utf8'),
      stat(filePath),
    ]);
    let session;
    try {
      session = JSON.parse(raw);
    } catch {
      session = extractSessionMetadata(raw);
    }
    const processName = session?.vmProcessName || session?.processName;
    if (
      !session?.sessionId
      || !processName
      || !Array.isArray(session.userSelectedFolders)
      || session.userSelectedFolders.length === 0
    ) {
      return null;
    }

    return {
      sessionId: session.sessionId,
      cliSessionId: session.cliSessionId || null,
      processName,
      hostLoopMode: session.hostLoopMode === true,
      cwd: typeof session.cwd === 'string' ? session.cwd : null,
      userSelectedFolders: session.userSelectedFolders.filter((folder) => typeof folder === 'string'),
      activityAt: Number(session.lastActivityAt) || fileStat.mtimeMs,
    };
  } catch {
    return null;
  }
}

function buildWorkspaceContext(session) {
  const roots = session.userSelectedFolders
    .filter((folder) => win32.isAbsolute(folder))
    .map((folder) => trimWindowsSeparator(win32.normalize(folder)));
  const basenames = roots.map((root) => win32.basename(root).toLowerCase());
  if (roots.length === 0 || new Set(basenames).size !== basenames.length) {
    return null;
  }

  return {
    sessionId: session.sessionId,
    cliSessionId: session.cliSessionId,
    processName: session.processName,
    hostLoopMode: session.hostLoopMode,
    cwd: session.cwd,
    mappings: roots.map((hostRoot) => ({
      hostRoot,
      mountRoot: posix.join('/sessions', session.processName, 'mnt', win32.basename(hostRoot)),
    })),
  };
}

function extractSessionMetadata(raw) {
  return {
    sessionId: extractJsonString(raw, 'sessionId'),
    cliSessionId: extractJsonString(raw, 'cliSessionId'),
    processName: extractJsonString(raw, 'processName'),
    vmProcessName: extractJsonString(raw, 'vmProcessName'),
    cwd: extractJsonString(raw, 'cwd'),
    userSelectedFolders: extractJsonArray(raw, 'userSelectedFolders'),
    lastActivityAt: extractJsonNumber(raw, 'lastActivityAt'),
    hostLoopMode: extractJsonBoolean(raw, 'hostLoopMode'),
  };
}

function extractJsonString(raw, name) {
  const match = raw.match(new RegExp(`"${name}"\\s*:\\s*("(?:\\\\.|[^"\\\\])*")`));
  if (!match) {
    return null;
  }
  try {
    return JSON.parse(match[1]);
  } catch {
    return null;
  }
}

function extractJsonNumber(raw, name) {
  const match = raw.match(new RegExp(`"${name}"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)`));
  return match ? Number(match[1]) : null;
}

function extractJsonBoolean(raw, name) {
  const match = raw.match(new RegExp(`"${name}"\\s*:\\s*(true|false)`));
  return match ? match[1] === 'true' : null;
}

function extractJsonArray(raw, name) {
  const keyMatch = new RegExp(`"${name}"\\s*:`).exec(raw);
  const start = keyMatch ? raw.indexOf('[', keyMatch.index + keyMatch[0].length) : -1;
  if (start < 0) {
    return null;
  }

  let inString = false;
  let escaped = false;
  let depth = 0;
  for (let index = start; index < raw.length; index += 1) {
    const char = raw[index];
    if (inString) {
      if (escaped) {
        escaped = false;
      } else if (char === '\\') {
        escaped = true;
      } else if (char === '"') {
        inString = false;
      }
      continue;
    }

    if (char === '"') {
      inString = true;
    } else if (char === '[') {
      depth += 1;
    } else if (char === ']') {
      depth -= 1;
      if (depth === 0) {
        try {
          return JSON.parse(raw.slice(start, index + 1));
        } catch {
          return null;
        }
      }
    }
  }
  return null;
}

function findExplicitSessionId(headers, metadata) {
  for (const name of SESSION_HEADER_NAMES) {
    const value = headers[name] ?? headers[name.toLowerCase()];
    if (typeof value === 'string' && value.trim()) {
      return value.trim();
    }
    if (Array.isArray(value) && typeof value[0] === 'string') {
      return value[0].trim();
    }
  }

  for (const name of SESSION_METADATA_NAMES) {
    const value = metadata?.[name];
    if (typeof value === 'string' && value.trim()) {
      return value.trim();
    }
  }
  return null;
}

function trimWindowsSeparator(value) {
  const parsed = win32.parse(value);
  if (value.toLowerCase() === parsed.root.toLowerCase()) {
    return value;
  }
  return value.replace(/[\\/]+$/, '');
}

function posixOrWindowsJoin(root, name) {
  return root.includes('\\') ? win32.join(root, name) : posix.join(root, name);
}
