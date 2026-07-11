import { decryptCatpawResponseBody } from './catpawCrypto.js';

const SERVICE_ROOT = 'https://catpaw.meituan.com';

export class CatpawAuthClient {
  constructor({ tenant, fetchFn = fetch, timeoutMs = 15_000 } = {}) {
    this.tenant = String(tenant || '').trim();
    this.fetchFn = fetchFn;
    this.timeoutMs = timeoutMs;
    if (!this.tenant) {
      throw new TypeError('tenant is required');
    }
  }

  async sendSms(mobile, deviceId) {
    const data = await this.#send('/api/login/sendSmsVerificationCode', {
      mobileNo: mobile,
      uuid: deviceId,
    }, null, 'SMS request');
    return {
      uuid: optionalString(data, 'uuid') || deviceId,
      requestCode: optionalString(data, 'requestCode'),
    };
  }

  async verifySms(mobile, code) {
    const data = await this.#send('/api/login/mobile/verify', {
      mobileNo: mobile,
      verificationCode: code,
    }, null, 'SMS verification');
    return {
      verified: optionalBoolean(data, 'verified', 'valid', 'success') ?? true,
      invitationCodeRequired: optionalBoolean(
        data,
        'invitationCodeRequired',
        'needInvitationCode',
        'invitationRequired',
      ) ?? false,
    };
  }

  async loginMobile(mobile, code, invitation = '') {
    const body = { mobileNo: mobile, verificationCode: code };
    if (invitation) {
      body.invitationCode = invitation;
    }
    const data = await this.#send('/api/login/mobile', body, null, 'mobile login');
    return this.#session(data);
  }

  async refresh(session) {
    const data = await this.#send('/api/login/refreshToken', {
      refreshToken: session.refreshToken,
    }, session.accessToken, 'token refresh');
    const refreshed = await this.#session(data, false);
    return {
      ...refreshed,
      userId: refreshed.userId || session.userId,
      accountLabel: refreshed.accountLabel || session.accountLabel,
    };
  }

  async getUserInfo(accessToken) {
    const data = await this.#send('/api/login/userInfo', undefined, accessToken, 'user info');
    return {
      userId: requiredString(data, 'userId', 'id', 'userMis', 'uid'),
      accountLabel: requiredString(
        data,
        'accountLabel',
        'accountName',
        'name',
        'nickname',
        'loginName',
      ),
    };
  }

  async #session(data, requireIdentity = true) {
    const session = {
      accessToken: requiredString(data, 'accessToken'),
      refreshToken: requiredString(data, 'refreshToken'),
      userId: optionalString(data, 'userId', 'id', 'userMis') || '',
      accountLabel: optionalString(data, 'accountLabel', 'accountName', 'name', 'nickname') || '',
      tenant: this.tenant,
      accessExpiresAt: optionalTimestamp(data, 'accessExpiresAt', 'accessTokenExpiresAt', 'expiresAt', 'expires'),
      refreshExpiresAt: optionalTimestamp(data, 'refreshExpiresAt', 'refreshTokenExpiresAt', 'refreshExpires'),
      refreshedAt: new Date().toISOString(),
    };
    if (!session.userId || !session.accountLabel) {
      const account = await this.getUserInfo(session.accessToken);
      session.userId = account.userId;
      session.accountLabel = account.accountLabel;
    }
    if (requireIdentity && (!session.userId || !session.accountLabel)) {
      throw new Error('Catpaw authentication returned incomplete account data');
    }
    return session;
  }

  async #send(path, body, accessToken, operation) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    timer.unref?.();
    try {
      const response = await this.fetchFn(`${SERVICE_ROOT}${path}`, {
        method: body === undefined ? 'GET' : 'POST',
        headers: {
          Accept: 'application/json',
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
          'client-type': 'CatPaw IDE',
          'ide-version': '2026.2.3',
          tenant: this.tenant,
          platform: 'win32-x64',
          ...(accessToken ? { 'Catpaw-Auth': accessToken } : {}),
        },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
        signal: controller.signal,
      });
      if (!response.ok) {
        throw new Error(`Catpaw ${operation} failed with HTTP ${response.status}`);
      }
      let text = await response.text();
      const encryptedKey = response.headers.get('encrypted-key');
      if (encryptedKey) {
        text = decryptCatpawResponseBody(text, encryptedKey);
      }
      let payload;
      try {
        payload = JSON.parse(text);
      } catch {
        throw new Error(`Catpaw ${operation} returned malformed data`);
      }
      if (Number(payload?.code) !== 0 || !payload?.data || typeof payload.data !== 'object') {
        throw new Error(`Catpaw ${operation} was rejected (code ${payload?.code ?? 'unknown'})`);
      }
      return payload.data;
    } catch (error) {
      if (error?.name === 'AbortError') {
        throw new Error(`Catpaw ${operation} timed out`);
      }
      throw error;
    } finally {
      clearTimeout(timer);
    }
  }
}

function requiredString(value, ...names) {
  const result = optionalString(value, ...names);
  if (!result) {
    throw new Error('Catpaw authentication response is missing required fields');
  }
  return result;
}

function optionalString(value, ...names) {
  for (const name of names) {
    if (typeof value?.[name] === 'string' && value[name].trim()) {
      return value[name].trim();
    }
  }
  return null;
}

function optionalBoolean(value, ...names) {
  for (const name of names) {
    if (typeof value?.[name] === 'boolean') {
      return value[name];
    }
  }
  return null;
}

function optionalTimestamp(value, ...names) {
  for (const name of names) {
    const raw = value?.[name];
    if (typeof raw === 'string' && !Number.isNaN(Date.parse(raw))) {
      return new Date(raw).toISOString();
    }
    if (typeof raw === 'number' && Number.isFinite(raw)) {
      return new Date(raw > 10_000_000_000 ? raw : raw * 1000).toISOString();
    }
  }
  return null;
}
