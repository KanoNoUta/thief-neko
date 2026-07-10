import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

export class UsageStore {
  constructor(filePath, { now = () => new Date(), retentionDays = 731 } = {}) {
    if (typeof now !== 'function') {
      throw new TypeError('now must be a function');
    }
    if (!Number.isSafeInteger(retentionDays) || retentionDays <= 0) {
      throw new RangeError('retentionDays must be a positive safe integer');
    }

    this.filePath = filePath || null;
    this.now = now;
    this.retentionDays = retentionDays;
    this.data = { version: 1, days: {} };
    this.loadPromise = null;
    this.pending = Promise.resolve();
  }

  async record({ inputTokens, outputTokens }, at = this.now()) {
    validateUsageValue('inputTokens', inputTokens);
    validateUsageValue('outputTokens', outputTokens);
    if (inputTokens === undefined && outputTokens === undefined) {
      return;
    }
    if (!(at instanceof Date) || Number.isNaN(at.getTime())) {
      throw new TypeError('usage date must be a valid Date');
    }

    const operation = this.pending.then(async () => {
      await this.#ensureLoaded();
      const date = formatLocalDate(at);
      const day = this.data.days[date] || { inputTokens: 0, outputTokens: 0, requests: 0 };
      if (inputTokens !== undefined) {
        day.inputTokens += inputTokens;
      }
      if (outputTokens !== undefined) {
        day.outputTokens += outputTokens;
      }
      day.requests += 1;
      this.data.days[date] = day;
      this.#prune();
      await this.#save();
    });
    this.pending = operation.catch(() => {});
    return operation;
  }

  async sumRange(start, end) {
    parseDateKey(start, 'start date');
    parseDateKey(end, 'end date');
    if (end < start) {
      throw new RangeError('end date cannot be before start date');
    }

    await this.pending;
    await this.#ensureLoaded();
    let inputTokens = 0;
    let outputTokens = 0;
    let requests = 0;
    let found = false;
    for (const [date, day] of Object.entries(this.data.days)) {
      if (date < start || date > end) {
        continue;
      }
      found = true;
      inputTokens += day.inputTokens || 0;
      outputTokens += day.outputTokens || 0;
      requests += day.requests || 0;
    }

    return {
      inputTokens: found ? inputTokens : null,
      outputTokens: found ? outputTokens : null,
      requests,
    };
  }

  async #ensureLoaded() {
    if (!this.filePath) {
      return;
    }
    if (!this.loadPromise) {
      this.loadPromise = readFile(this.filePath, 'utf8')
        .then((text) => {
          const parsed = JSON.parse(text);
          if (!parsed || parsed.version !== 1 || !parsed.days || typeof parsed.days !== 'object') {
            throw new Error('usage history file has an unsupported format');
          }
          this.data = parsed;
        })
        .catch((error) => {
          if (error.code !== 'ENOENT') {
            throw error;
          }
        });
    }
    await this.loadPromise;
  }

  #prune() {
    const cutoff = new Date(this.now());
    cutoff.setHours(0, 0, 0, 0);
    cutoff.setDate(cutoff.getDate() - (this.retentionDays - 1));
    const cutoffKey = formatLocalDate(cutoff);
    for (const date of Object.keys(this.data.days)) {
      if (date < cutoffKey) {
        delete this.data.days[date];
      }
    }
  }

  async #save() {
    if (!this.filePath) {
      return;
    }
    await mkdir(dirname(this.filePath), { recursive: true });
    const temporaryPath = `${this.filePath}.${process.pid}.${Date.now()}.tmp`;
    await writeFile(temporaryPath, JSON.stringify(this.data), 'utf8');
    await rename(temporaryPath, this.filePath);
  }
}

export function formatLocalDate(date) {
  const year = String(date.getFullYear()).padStart(4, '0');
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

export function parseDateKey(value, label = 'date') {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    throw new RangeError(`${label} must use YYYY-MM-DD`);
  }
  const [year, month, day] = value.split('-').map(Number);
  const parsed = new Date(year, month - 1, day);
  if (
    parsed.getFullYear() !== year
    || parsed.getMonth() !== month - 1
    || parsed.getDate() !== day
  ) {
    throw new RangeError(`${label} must be a valid calendar date`);
  }
  return parsed;
}

function validateUsageValue(name, value) {
  if (value === undefined) {
    return;
  }
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) {
    throw new RangeError(`${name} must be a non-negative finite number`);
  }
}
