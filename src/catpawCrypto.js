import crypto from 'node:crypto';

const XOR_KEY = 'ThisIsMyXorKey';
const PUBLIC_KEY_ENCODED =
  'eUVEXmQxCD4RIVIbMDsYISpTAjYUVHVCX2ZvNB0hKzojMgM7PwQDIw4QE1EeQwsyHDweLjMEJjgFUCg+ADoPOj8kMQo0PBUdGTgxF2Y8DxwEMiobI1wqIi0QBnMLDjc7ExQQWV49OAIEMG47GD4QKhIkUAYvQz0SLl4WGxwvOAoMOy0SAz9hNR4hKlI3OAAgegA7NWk3XX4cQHspYzYaWCw4Ky0LIgABFyceCgsiOg4sHgZ6FC8dH1gwekIVNBwbNj4rODcOORAPBiIyEgslOFYpDgQKFS4kOQwiLj1BXBQEGlskGEUFFW4dPwYkFw0HXBcgNx1WNRpEAFc9NztcMHwFJhEeLEc/Vy0lJgUxezl1GBs2OQMODiYsWhcjJEcLGQ4Bc0o0NjEsO3FDOTINOgYfNjcfGBwmBB4sPg0/NSkRSBI8AgUnHSwLOlslAD03YVo4CjoYOg9pADF+VTIWYlwYH0EIKGo+WQMiKzkRHwQNIgEuKFoEHA0WZywqNhMkdVYpNyIOFhQ+HhFcLhUMDw8ECCwMID05IEYlJT5MCAN4CBIwECk4Mgt5YFR1Ql8OKz10ODwxBToOWRMqK2ZIVHlF';
const PRIVATE_KEY_ENCODED =
  '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';

const PUBLIC_KEY = xorDecode(PUBLIC_KEY_ENCODED);
const PRIVATE_KEY = xorDecode(PRIVATE_KEY_ENCODED);

export function encryptCatpawRequestBody(value, headers) {
  const aesKey = crypto.randomBytes(16);
  headers['encrypted-key'] = crypto.publicEncrypt({
    key: PUBLIC_KEY,
    padding: crypto.constants.RSA_PKCS1_OAEP_PADDING,
    oaepHash: 'sha1',
  }, Buffer.from(aesKey.toString('base64'))).toString('base64');
  return encryptAes(value, aesKey);
}

export function decryptCatpawResponseBody(value, encryptedKey) {
  if (!value || !encryptedKey) {
    return value;
  }

  const aesKeyBase64 = crypto.privateDecrypt({
    key: PRIVATE_KEY,
    padding: crypto.constants.RSA_PKCS1_OAEP_PADDING,
    oaepHash: 'sha1',
  }, Buffer.from(encryptedKey, 'base64')).toString();
  return decryptAes(value, Buffer.from(aesKeyBase64, 'base64'));
}

function encryptAes(value, key) {
  const text = typeof value === 'string' ? value : JSON.stringify(value);
  const cipher = crypto.createCipheriv('aes-128-ecb', key, null);
  let encrypted = cipher.update(text, 'utf8', 'base64');
  encrypted += cipher.final('base64');
  return encrypted;
}

function decryptAes(value, key) {
  const decipher = crypto.createDecipheriv('aes-128-ecb', key, null);
  let decrypted = decipher.update(value, 'base64', 'utf8');
  decrypted += decipher.final('utf8');
  return decrypted;
}

function xorDecode(value) {
  const input = Buffer.from(value, 'base64');
  const output = Buffer.alloc(input.length);

  for (let i = 0; i < input.length; i += 1) {
    output[i] = input[i] ^ XOR_KEY.charCodeAt(i % XOR_KEY.length);
  }

  return output.toString('utf8');
}
