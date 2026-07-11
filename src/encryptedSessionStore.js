import { randomBytes, createCipheriv, createDecipheriv } from 'node:crypto';
import { chmod, mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

export class EncryptedSessionStore {
  constructor({ sessionPath, keyPath }) {
    this.sessionPath = sessionPath;
    this.keyPath = keyPath;
  }

  async load() {
    const key = await this.#key(false);
    const envelope = JSON.parse(await readFile(this.sessionPath, 'utf8'));
    if (envelope.version !== 1) {
      throw new Error('Unsupported encrypted session version');
    }
    const nonce = Buffer.from(envelope.nonce, 'base64');
    const tag = Buffer.from(envelope.tag, 'base64');
    const ciphertext = Buffer.from(envelope.ciphertext, 'base64');
    const decipher = createDecipheriv('aes-256-gcm', key, nonce);
    decipher.setAuthTag(tag);
    return JSON.parse(Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString('utf8'));
  }

  async save(session) {
    const key = await this.#key(true);
    const nonce = randomBytes(12);
    const cipher = createCipheriv('aes-256-gcm', key, nonce);
    const ciphertext = Buffer.concat([
      cipher.update(JSON.stringify(session), 'utf8'),
      cipher.final(),
    ]);
    const envelope = JSON.stringify({
      version: 1,
      nonce: nonce.toString('base64'),
      tag: cipher.getAuthTag().toString('base64'),
      ciphertext: ciphertext.toString('base64'),
    });
    await mkdir(dirname(this.sessionPath), { recursive: true, mode: 0o700 });
    const temporary = `${this.sessionPath}.tmp`;
    await writeFile(temporary, envelope, { mode: 0o600 });
    await chmod(temporary, 0o600);
    await rename(temporary, this.sessionPath);
  }

  async #key(create) {
    try {
      const encoded = (await readFile(this.keyPath, 'utf8')).trim();
      const key = Buffer.from(encoded, 'base64');
      if (key.length !== 32) {
        throw new Error('Invalid session key');
      }
      return key;
    } catch (error) {
      if (!create || error?.code !== 'ENOENT') {
        throw error;
      }
      await mkdir(dirname(this.keyPath), { recursive: true, mode: 0o700 });
      const key = randomBytes(32);
      await writeFile(this.keyPath, key.toString('base64'), { mode: 0o600, flag: 'wx' });
      await chmod(this.keyPath, 0o600);
      return key;
    }
  }
}
