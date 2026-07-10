const DEFAULT_POLL_INTERVAL_MS = 5_000;
const DEFAULT_REFRESH_ATTEMPTS = 3;
const DEFAULT_REFRESH_DELAY_MS = 250;

export class CatpawCredentialManager {
  constructor({
    token,
    cookie = '',
    userMis = '',
    readSession,
    pollIntervalMs = DEFAULT_POLL_INTERVAL_MS,
    refreshAttempts = DEFAULT_REFRESH_ATTEMPTS,
    refreshDelayMs = DEFAULT_REFRESH_DELAY_MS,
    sleep = delay,
    setIntervalFn = setInterval,
    clearIntervalFn = clearInterval,
    onRefresh = () => {},
  }) {
    if (typeof readSession !== 'function') {
      throw new TypeError('readSession must be a function');
    }
    if (!Number.isSafeInteger(pollIntervalMs) || pollIntervalMs <= 0) {
      throw new RangeError('pollIntervalMs must be a positive safe integer');
    }
    if (!Number.isSafeInteger(refreshAttempts) || refreshAttempts <= 0) {
      throw new RangeError('refreshAttempts must be a positive safe integer');
    }
    if (!Number.isSafeInteger(refreshDelayMs) || refreshDelayMs < 0) {
      throw new RangeError('refreshDelayMs must be a non-negative safe integer');
    }

    this.state = {
      token: String(token || '').trim(),
      cookie: String(cookie || ''),
      userMis: String(userMis || '').trim(),
      generation: 0,
    };
    this.readSession = readSession;
    this.pollIntervalMs = pollIntervalMs;
    this.refreshAttempts = refreshAttempts;
    this.refreshDelayMs = refreshDelayMs;
    this.sleep = sleep;
    this.setIntervalFn = setIntervalFn;
    this.clearIntervalFn = clearIntervalFn;
    this.onRefresh = onRefresh;
    this.timer = null;
    this.readPromise = null;
    this.refreshPromise = null;
  }

  snapshot() {
    return { ...this.state };
  }

  async poll() {
    const before = this.state.generation;
    await this.#readAndApply();
    return this.state.generation !== before;
  }

  async refreshAfterUnauthorized(usedToken) {
    if (this.state.token !== usedToken) {
      return true;
    }

    while (this.refreshPromise) {
      await this.refreshPromise;
      if (this.state.token !== usedToken) {
        return true;
      }
    }

    const refresh = this.#refreshUntilChanged(usedToken);
    this.refreshPromise = refresh;
    try {
      return await refresh;
    } finally {
      if (this.refreshPromise === refresh) {
        this.refreshPromise = null;
      }
    }
  }

  start() {
    if (this.timer) {
      return;
    }
    this.timer = this.setIntervalFn(
      () => this.poll().catch(() => false),
      this.pollIntervalMs,
    );
    this.timer?.unref?.();
  }

  stop() {
    if (!this.timer) {
      return;
    }
    this.clearIntervalFn(this.timer);
    this.timer = null;
  }

  async #refreshUntilChanged(usedToken) {
    for (let attempt = 0; attempt < this.refreshAttempts; attempt += 1) {
      await this.#readAndApply();
      if (this.state.token !== usedToken) {
        return true;
      }
      if (attempt + 1 < this.refreshAttempts && this.refreshDelayMs > 0) {
        await this.sleep(this.refreshDelayMs);
      }
    }
    return false;
  }

  async #readAndApply() {
    let session;
    try {
      session = await this.#readOnce();
    } catch {
      return false;
    }
    const token = String(session?.token || '').trim();
    if (!token) {
      return false;
    }
    const userMis = String(session?.userMis || this.state.userMis).trim();
    if (token === this.state.token && userMis === this.state.userMis) {
      return false;
    }

    const previousToken = this.state.token;
    this.state = {
      token,
      cookie: replaceCookieToken(this.state.cookie, previousToken, token),
      userMis,
      generation: this.state.generation + 1,
    };
    this.onRefresh({ generation: this.state.generation });
    return true;
  }

  async #readOnce() {
    if (!this.readPromise) {
      this.readPromise = Promise.resolve()
        .then(() => this.readSession())
        .finally(() => { this.readPromise = null; });
    }
    return this.readPromise;
  }
}

function replaceCookieToken(cookie, previousToken, nextToken) {
  if (!cookie || !previousToken || !cookie.includes(previousToken)) {
    return cookie;
  }
  return cookie.split(previousToken).join(nextToken);
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
