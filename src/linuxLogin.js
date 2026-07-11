import { randomUUID } from 'node:crypto';
import { CatpawAuthClient } from './catpawAuthClient.js';
import { EncryptedSessionStore } from './encryptedSessionStore.js';

const command = process.argv[2];
const tenant = process.env.CATPAW_TENANT || '5282fa6645';
const mobile = String(process.env.CATPAW_PHONE || '').trim();
const sessionPath = process.env.CATPAW_SESSION_PATH || '/var/lib/thief-neko/session.enc';
const keyPath = process.env.CATPAW_SESSION_KEY_PATH || '/etc/thief-neko/session.key';
const client = new CatpawAuthClient({ tenant });
const store = new EncryptedSessionStore({ sessionPath, keyPath });

try {
  if (!mobile) {
    throw new Error('CATPAW_PHONE is required');
  }
  if (command === 'send') {
    const challenge = await client.sendSms(mobile, randomUUID());
    console.log(JSON.stringify({
      ok: true,
      requiresYoda: Boolean(challenge.requestCode),
      uuid: challenge.uuid,
    }));
  } else if (command === 'complete') {
    const code = String(process.env.CATPAW_SMS_CODE || '').trim();
    const invitation = String(process.env.CATPAW_INVITATION_CODE || '').trim();
    if (!code) {
      throw new Error('CATPAW_SMS_CODE is required');
    }
    const verification = await client.verifySms(mobile, code);
    if (!verification.verified) {
      throw new Error('Catpaw rejected the SMS code');
    }
    if (verification.invitationCodeRequired && !invitation) {
      throw new Error('CATPAW_INVITATION_CODE is required');
    }
    const session = await client.loginMobile(mobile, code, invitation);
    await store.save(session);
    console.log(JSON.stringify({ ok: true, account: session.accountLabel }));
  } else {
    throw new Error('Usage: node src/linuxLogin.js send|complete');
  }
} catch (error) {
  console.error(JSON.stringify({ ok: false, error: error.message }));
  process.exitCode = 1;
}
