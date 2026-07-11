export class HeadlessCredentialProvider {
  constructor({ client, store, refreshIntervalMs = 30 * 60 * 1000 }) {
    this.client = client;
    this.store = store;
    this.refreshIntervalMs = refreshIntervalMs;
    this.session = null;
    this.generation = 0;
    this.loadPromise = null;
    this.refreshPromise = null;
    this.timer = null;
  }

  async snapshot() {
    await this.#load();
    return this.#snapshot();
  }

  async refreshAfterUnauthorized(usedToken) {
    await this.#load();
    if (this.session.accessToken !== usedToken) {
      return true;
    }
    if (!this.refreshPromise) {
      this.refreshPromise = this.#refresh().finally(() => { this.refreshPromise = null; });
    }
    await this.refreshPromise;
    return this.session.accessToken !== usedToken;
  }

  start() {
    if (this.timer) {
      return;
    }
    this.timer = setInterval(() => this.#refreshIfExpiring().catch(() => {}), this.refreshIntervalMs);
    this.timer.unref?.();
  }

  stop() {
    clearInterval(this.timer);
    this.timer = null;
  }

  async #load() {
    if (this.session) {
      return;
    }
    if (!this.loadPromise) {
      this.loadPromise = this.store.load()
        .then((session) => { this.session = session; })
        .finally(() => { this.loadPromise = null; });
    }
    await this.loadPromise;
  }

  async #refreshIfExpiring() {
    await this.#load();
    const expiresAt = Date.parse(this.session.accessExpiresAt || '');
    if (Number.isFinite(expiresAt) && expiresAt - Date.now() > 5 * 60 * 1000) {
      return;
    }
    if (!this.refreshPromise) {
      this.refreshPromise = this.#refresh().finally(() => { this.refreshPromise = null; });
    }
    await this.refreshPromise;
  }

  async #refresh() {
    const refreshed = await this.client.refresh(this.session);
    await this.store.save(refreshed);
    this.session = refreshed;
    this.generation += 1;
  }

  #snapshot() {
    const token = this.session.accessToken;
    return {
      token,
      userMis: this.session.userId,
      cookie: `1d47d6ff96_passportid=${token}; f32a546874_ssoid=${token}`,
      generation: this.generation,
    };
  }
}
