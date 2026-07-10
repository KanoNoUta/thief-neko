import { DatabaseSync } from 'node:sqlite';
import { execFile } from 'node:child_process';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const scriptPath = fileURLToPath(import.meta.url);

export function readCatpawSession(env = process.env) {
  const statePath = path.join(
    env.APPDATA,
    'CatPawAI',
    'User',
    'globalStorage',
    'state.vscdb',
  );
  const db = new DatabaseSync(statePath, { readOnly: true });

  try {
    db.exec('PRAGMA busy_timeout = 5000');
    const row = db.prepare(
      "select value from ItemTable where key='catpaw.mt-authentication'",
    ).get();
    if (!row?.value) {
      throw new Error('Catpaw authentication state was not found');
    }

    const outer = JSON.parse(row.value);
    const auth = JSON.parse(outer['mt.auth']);
    const session = auth.sessions?.[0];
    if (!session?.accessToken || !session?.account?.id) {
      throw new Error('Catpaw authentication session is incomplete');
    }

    return {
      token: session.accessToken,
      userMis: session.account.id,
    };
  } finally {
    db.close();
  }
}

export async function readCatpawSessionAsync(
  env = process.env,
  runStateReader = runStateReaderProcess,
) {
  const { stdout } = await runStateReader(env);
  const session = JSON.parse(stdout);
  if (!session?.token || !session?.userMis) {
    throw new Error('Catpaw authentication session is incomplete');
  }
  return { token: session.token, userMis: session.userMis };
}

async function runStateReaderProcess(env) {
  return execFileAsync(process.execPath, [scriptPath], {
    env,
    windowsHide: true,
    timeout: 4_000,
    maxBuffer: 64 * 1024,
  });
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.stdout.write(JSON.stringify(readCatpawSession()));
}
