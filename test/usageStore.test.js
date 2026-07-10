import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { UsageStore } from '../src/usageStore.js';

test('UsageStore persists daily usage and sums inclusive ranges', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'catapi-usage-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const filePath = join(directory, 'usage.json');
  const store = new UsageStore(filePath, { now: () => new Date(2026, 6, 10, 12) });

  await Promise.all([
    store.record({ inputTokens: 1_000_000, outputTokens: 200_000 }, new Date(2026, 5, 30, 23)),
    store.record({ inputTokens: 2_000_000, outputTokens: 300_000 }, new Date(2026, 6, 1, 1)),
    store.record({ inputTokens: 4_000_000, outputTokens: 500_000 }, new Date(2026, 6, 10, 8)),
  ]);

  const reloaded = new UsageStore(filePath, { now: () => new Date(2026, 6, 10, 12) });
  assert.deepEqual(await reloaded.sumRange('2026-06-30', '2026-07-01'), {
    inputTokens: 3_000_000,
    outputTokens: 500_000,
    requests: 2,
  });
  assert.deepEqual(await reloaded.sumRange('2026-07-10', '2026-07-10'), {
    inputTokens: 4_000_000,
    outputTokens: 500_000,
    requests: 1,
  });

  const persisted = JSON.parse(await readFile(filePath, 'utf8'));
  assert.deepEqual(Object.keys(persisted.days), ['2026-06-30', '2026-07-01', '2026-07-10']);
  assert.equal(JSON.stringify(persisted).includes('token'), false);
});

test('UsageStore returns unavailable token values for an empty range', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'catapi-usage-empty-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const store = new UsageStore(join(directory, 'usage.json'));

  assert.deepEqual(await store.sumRange('2026-01-01', '2026-01-07'), {
    inputTokens: null,
    outputTokens: null,
    requests: 0,
  });
});

test('UsageStore validates dates and numeric usage', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'catapi-usage-valid-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const store = new UsageStore(join(directory, 'usage.json'));

  await assert.rejects(() => store.record({ inputTokens: -1, outputTokens: 2 }), /inputTokens/);
  await assert.rejects(() => store.sumRange('2026-02-30', '2026-03-01'), /start date/);
  await assert.rejects(() => store.sumRange('2026-03-02', '2026-03-01'), /before start/);
});
