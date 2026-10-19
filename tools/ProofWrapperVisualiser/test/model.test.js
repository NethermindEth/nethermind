// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Run with: node --test tools/ProofWrapperVisualiser/test/
const test = require('node:test');
const assert = require('node:assert/strict');
const stages = require('../js/stages.js');
const { Store, aggregate, percentileSeries, eventMatches } = require('../js/model.js');

const W = '0x' + 'aa'.repeat(32), TX = '0x' + 'bb'.repeat(32);
const A = 'a'.repeat(128), B = 'b'.repeat(128);

/** A two-node propagation: node a offers W, node b fetches it, recovers its envelope, validates and relays it; then inclusion. */
function propagation(store) {
  store.node('a', { name: 'a', enode: `enode://${A}@1.1.1.1:1` });
  store.node('b', { name: 'b', enode: `enode://${B}@1.1.1.2:1` });
  store.ingest('a', [
    { seq: '0x1', time: 1000, type: 'submitted', wrapper: W, message: 'local', mode: 0, length: 6355, transactions: [TX], dependencies: ['leanSPHINCS:0x1:0x2'] },
    { seq: '0x2', time: 1005, type: 'offered', wrapper: W, result: 'local aggregate', transactions: [TX] },
    { seq: '0x3', time: 1006, type: 'announced', wrapper: W, peerId: '0x' + B, message: 'NewProofWrapperHashes' },
  ]);
  store.ingest('b', [
    { seq: '0x1', time: 1010, type: 'announcement-received', wrapper: W, peerId: '0x' + A, result: 'queued', length: 6355 },
    { seq: '0x2', time: 1011, type: 'wrapper-requested', wrapper: W, peerId: '0x' + A, requestId: '0x1' },
    { seq: '0x3', time: 1030, type: 'wrapper-received', wrapper: W, peerId: '0x' + A, result: 'matched hash and announced length; queued for validation' },
    { seq: '0x4', time: 1031, type: 'validation-started', wrapper: W, count: 1, mode: 0, transactions: [TX] },
    { seq: '0x5', time: 1032, type: 'envelope-requested', wrapper: W, peerId: '0x' + A, requestId: '0x2', count: 1 },
    { seq: '0x6', time: 1040, type: 'envelope-response', wrapper: W, peerId: '0x' + A, result: 'returned 0 of 1', count: 0, detail: '1 omitted before the last returned: unavailable here, try another announcer' },
    { seq: '0x7', time: 1045, type: 'envelope-recovery', wrapper: W, result: 'complete', durationMs: 14, count: 2 },
    { seq: '0x8', time: 1050, type: 'verdict', wrapper: W, result: 'Valid', peerId: '0x' + A },
    { seq: '0x9', time: 1052, type: 'offered', wrapper: W, result: 'validated', transactions: [TX] },
  ]);
  store.ingest('a', [{ seq: '0x4', time: 13000, type: 'included', transactions: [TX], block: '0x7' }]);
}

test('stages name each step after its EIP-8437 message or rule', () => {
  const d = stages.describe({ type: 'wrapper-requested' });
  assert.equal(d.stage, 'requested');
  assert.equal(d.message, 'GetProofWrappers (0x17)');
  assert.match(stages.describe({ type: 'verdict', result: 'GraceRefused' }).rule, /grace|local refusal/i);
  assert.equal(stages.describe({ type: 'verdict', result: 'Invalid' }).stage, 'rejected');
  assert.equal(stages.describe({ type: 'announcement-received', result: "dropped: peer's share of pending announcements used" }).stage, 'dropped');
  for (const type of Object.keys(stages.TYPES)) assert.ok(stages.describe({ type, result: '' }).label, type);
});

test('indexes a wrapper per node and links transaction events to it', () => {
  const store = new Store();
  propagation(store);
  const w = store.wrappers.get(W);
  assert.equal(w.mode, 0);
  assert.deepEqual(w.txs, [TX]);
  assert.equal(w.perNode.size, 2);
  assert.equal(w.outcome, 'included');
  assert.equal(w.included.block, 7);
  const b = w.perNode.get('b');
  assert.equal(b.rounds, 1);
  assert.equal(b.omitted, 1);
  assert.equal(b.verdict, 'Valid');
  assert.equal(store.peerNode('0x' + A.toUpperCase()), 'a');
});

test('transaction events seen before their wrapper are linked once it arrives', () => {
  const store = new Store();
  store.ingest('a', [{ seq: '0x1', time: 5, type: 'included', transactions: [TX], block: '0x2' }]);
  store.ingest('a', [{ seq: '0x2', time: 1, type: 'offered', wrapper: W, transactions: [TX] }]);
  assert.equal(store.wrappers.get(W).outcome, 'included');
});

test('aggregates per-hop latency, recovery and omissions', () => {
  const store = new Store();
  propagation(store);
  const agg = aggregate(store, {}, { now: 14000, windowMs: 20000, binMs: 1000 });
  assert.equal(agg.hop.n, 1);
  assert.equal(agg.hop.p50, 46, 'a announced to b at 1006, b offered at 1052');
  assert.equal(agg.fetch.p50, 20);
  assert.equal(agg.recovery.p50, 14);
  assert.equal(agg.counts.omissions, 1);
  assert.equal(agg.counts.envelopeRequests, 1);
  assert.equal(agg.outcomes.included, 1);
  assert.equal(agg.slowest[0].w, W);
  const requested = agg.heat[stages.STAGE_INDEX.requested];
  assert.equal(requested.reduce((a, b) => a + b, 0), 1);
});

test('filters by node, mode, stage, outcome and text', () => {
  const store = new Store();
  propagation(store);
  const all = [...store.events()];
  const count = f => all.filter(e => eventMatches(store, e, f)).length;
  assert.equal(count({ node: 'b' }), 9);
  assert.equal(count({ mode: '1' }), 0);
  assert.equal(count({ mode: '0', stage: 'recovery' }), 4);
  assert.equal(count({ outcome: 'included', text: 'omitted' }), 1);
  assert.equal(aggregate(store, { mode: '1' }, { now: 14000, windowMs: 20000, binMs: 1000 }).wrappers, 0);
});

test('keeps the client buffer bounded, evicting the oldest events and wrappers', () => {
  const store = new Store({ capacity: 100, maxWrappers: 10 });
  for (let i = 0; i < 50; i++) {
    const hash = '0x' + i.toString(16).padStart(64, '0');
    store.ingest('a', [{ seq: i * 3 + 1, time: i, type: 'announcement-received', wrapper: hash }, { seq: i * 3 + 2, time: i, type: 'wrapper-requested', wrapper: hash }, { seq: i * 3 + 3, time: i, type: 'gauges', detail: 'pending=1' }]);
  }
  assert.equal(store.size, 100);
  assert.equal(store.evicted, 50);
  assert.equal(store.wrappers.size, 10);
  assert.equal(store.get(store.minGid - 1), undefined);
  assert.equal([...store.events()].length, 100);
  assert.equal(store.nodes.get('a').after, 150);
});

test('ignores malformed field types from hand-edited captures', () => {
  const store = new Store();
  store.ingest('a', [{ seq: 1, time: 1, type: 'offered', wrapper: { evil: 1 }, transactions: [1, 2], detail: ['x'] }]);
  const [e] = store.events();
  assert.equal(e.wrapper, undefined);
  assert.equal(e.transactions, undefined);
  assert.equal(e.detail, undefined);
});

test('exports a capture that loads back to the same wrappers', () => {
  const store = new Store();
  propagation(store);
  const copy = new Store();
  const capture = JSON.parse(JSON.stringify(store.toJSON()));
  for (const n of capture.nodes) copy.node(n.key, n);
  for (const [key, list] of Object.entries(capture.events)) copy.ingest(key, list);
  assert.equal(capture.format, 'eip8437-visualiser/1');
  assert.deepEqual(aggregate(copy, {}, { now: 14000, windowMs: 20000, binMs: 1000 }).hop.p50, 46);
});

test('percentile series per bin', () => {
  const series = percentileSeries([[0, 1], [10, 3], [20, 100], [1500, 7]], 0, 2, 1000);
  assert.deepEqual(series[0], { n: 3, p50: 3, p95: 100 });
  assert.deepEqual(series[1], { n: 1, p50: 7, p95: 7 });
});

test('parses node lists, kurtosis enclave inspect and port print output', () => {
  const { parseNodes } = require('../js/sources.js');
  const inspect = [
    '020bdf659e0d   el-1-nethermind-lighthouse                       engine-rpc: 8551/tcp -> 127.0.0.1:41551        RUNNING',
    '                                                                rpc: 8545/tcp -> 127.0.0.1:41445',
    '1a73d44f21cd   el-2-nethermind-lighthouse                       rpc: 8545/tcp -> http://127.0.0.1:41545        RUNNING',
  ].join('\n');
  assert.deepEqual(parseNodes(inspect), [{ name: 'el-1', url: 'http://127.0.0.1:41445' }, { name: 'el-2', url: 'http://127.0.0.1:41545' }]);
  assert.deepEqual(parseNodes('el-3=http://10.0.0.3:8545\nhttp://127.0.0.1:41945\n127.0.0.1:1', 3),
    [{ name: 'el-3', url: 'http://10.0.0.3:8545' }, { name: 'node-5', url: 'http://127.0.0.1:41945' }, { name: 'node-6', url: 'http://127.0.0.1:1' }]);
});

test('drops events already ingested from the same feed, and starts over when the node restarted', () => {
  const store = new Store();
  const page = [{ seq: '0x1', time: 1, type: 'offered', wrapper: W }, { seq: '0x2', time: 2, type: 'announced', wrapper: W }];
  store.ingest('a', page, 0);
  store.ingest('a', page, 0);
  assert.equal(store.size, 2, 'a page fetched twice is ingested once');
  assert.equal(store.duplicates, 2);
  store.ingest('a', [{ seq: '0x1', time: 10, type: 'offered', wrapper: W }], 1);
  assert.equal(store.size, 3, 'after a restart the node numbers from 1 again');
  assert.deepEqual(store.ingest('a', [{ seq: '0x3', time: 3, type: 'served', wrapper: W }], 0), [], 'late batches from before the restart are dropped');
  assert.equal(store.wrappers.get(W).events.length, 3);
});

test('a rejected copy is not shown as included when a valid wrapper carried its transaction', () => {
  const store = new Store();
  const BAD = '0x' + 'cc'.repeat(32);
  store.ingest('a', [
    { seq: 1, time: 1, type: 'submitted', wrapper: BAD, message: 'local', transactions: [TX], mode: 0 },
    { seq: 2, time: 2, type: 'proof-verified', wrapper: BAD, result: 'invalid', detail: 'a wrapper dependency proof failed verification' },
    { seq: 3, time: 3, type: 'submitted', wrapper: W, message: 'eth/73', transactions: [TX], mode: 0 },
    { seq: 4, time: 4, type: 'admitted', wrapper: W, transactions: [TX], result: 'Accepted' },
    { seq: 5, time: 5, type: 'offered', wrapper: W, result: 'validated', transactions: [TX] },
    { seq: 6, time: 9, type: 'included', transactions: [TX], block: '0x9' },
  ]);
  const bad = store.wrappers.get(BAD), good = store.wrappers.get(W);
  assert.equal(bad.outcome, 'rejected');
  assert.equal(bad.included, undefined);
  assert.deepEqual(bad.txIncluded, { block: 9, t: 9 });
  assert.equal(good.outcome, 'included');
  assert.deepEqual(require('../js/model.js').carriers(store, bad), [W]);
  const steps = require('../js/model.js').groupSteps(bad, bad.events.map(g => store.get(g)));
  assert.deepEqual(steps.map(s => [s.e.type, s.own]), [['submitted', true], ['proof-verified', true], ['included', false]]);
});

test('collapses repeated steps on a node into one with a count', () => {
  const { groupSteps } = require('../js/model.js');
  const w = { hash: W };
  const e = (node, type, t, peer, result) => ({ node, type, t, peer, result, wrapper: W });
  const steps = groupSteps(w, [e('a', 'announced', 1, 'p1'), e('b', 'announcement-received', 2, 'a', 'queued'), e('a', 'announced', 3, 'p2'),
    e('a', 'announced', 4, 'p3'), e('a', 'served', 5, 'p1', 'served'), e('a', 'announced', 6, 'p4')]);
  assert.deepEqual(steps.map(s => [s.e.node, s.e.type, s.members.length]),
    [['a', 'announced', 3], ['b', 'announcement-received', 1], ['a', 'served', 1], ['a', 'announced', 1]]);
});

test('aligned windows keep bin edges fixed as time passes', () => {
  const { alignedWindow } = require('../js/model.js');
  const a = alignedWindow(10_250, 4000, 1000), b = alignedWindow(10_900, 4000, 1000);
  assert.deepEqual(a, { start: 7000, bins: 4, binMs: 1000, end: 11000 });
  assert.deepEqual(b, a);
  assert.equal(alignedWindow(11_000, 4000, 1000).start, 8000);
});

test('prepares series sorted, windowed and decimated, keeping spikes, with gaps and nearest lookup', () => {
  const { prepareSeries, gaps, nearest } = require('../js/model.js');
  assert.deepEqual(prepareSeries([[3, 1], [1, 2], [2, NaN], null, [9, 5], [2, 4]], 0, 5), [[1, 2], [2, 4], [3, 1]]);
  const dense = Array.from({ length: 10_000 }, (_, i) => [i, i === 5_000 ? 1_000 : 1]);
  const decimated = prepareSeries(dense, 0, 10_000, 300);
  assert.ok(decimated.length <= 300, `${decimated.length} points`);
  assert.ok(decimated.some(p => p[1] === 1_000), 'the spike survives decimation');
  assert.ok(decimated.every((p, i) => i === 0 || p[0] >= decimated[i - 1][0]), 'still sorted');
  assert.deepEqual(gaps([[0, 1], [10, 1], [50, 1], [60, 1]], 15), [2]);
  assert.deepEqual(nearest([[0, 1], [10, 2], [50, 3]], 31), [50, 3]);
  assert.deepEqual(nearest([[0, 1], [10, 2], [50, 3]], 29), [10, 2]);
  assert.equal(nearest([], 1), undefined);
});

test('a hop to a node that reconnected starts at the re-announcement, not the original offer', () => {
  const store = new Store();
  store.node('a', { enode: `enode://${A}@1.1.1.1:1` });
  store.node('b', { enode: `enode://${B}@1.1.1.2:1` });
  store.ingest('a', [
    { seq: 1, time: 1000, type: 'offered', wrapper: W, result: 'local aggregate' },
    { seq: 2, time: 600000, type: 'announced', wrapper: W, peerId: '0x' + B },
  ]);
  store.ingest('b', [
    { seq: 1, time: 600010, type: 'announcement-received', wrapper: W, peerId: '0x' + A, result: 'queued' },
    { seq: 2, time: 600060, type: 'offered', wrapper: W, result: 'validated' },
  ]);
  assert.equal(aggregate(store, {}, { now: 601000, windowMs: 700000, binMs: 1000 }).hop.p50, 60);
});

test('builds one row per node and phase with outcome and summary', () => {
  const { phases } = require('../js/model.js');
  const store = new Store();
  propagation(store);
  const w = store.wrappers.get(W);
  const rows = phases(w, w.events.map(g => store.get(g)).sort((a, b) => a.t - b.t));
  assert.deepEqual(rows.map(r => `${r.node}:${r.phase}:${r.outcome}`), [
    'a:originate:ok', 'a:relay:ok', 'b:announce:ok', 'b:fetch:ok', 'b:recover:ok', 'b:validate:ok', 'b:relay:ok', 'a:include:ok']);
  const recover = rows.find(r => r.phase === 'recover');
  assert.equal(recover.end - recover.start, 14, 'validation started at 1031, recovery finished at 1045');
  assert.match(recover.summary, /^1 GetPooledTransactions round, 1 with omissions/);
  assert.equal(recover.events.length, 4, 'validation-started with missing envelopes, request, response and the summary');
});

test('a rejected copy shows a failed Validate row and inclusion only as a shared-transaction note', () => {
  const { phases } = require('../js/model.js');
  const store = new Store();
  const BAD = '0x' + 'cc'.repeat(32);
  store.ingest('a', [
    { seq: 1, time: 1, type: 'submitted', wrapper: BAD, message: 'local', transactions: [TX], mode: 1 },
    { seq: 2, time: 98, type: 'proof-verified', wrapper: BAD, result: 'invalid', detail: 'a wrapper dependency proof failed verification' },
    { seq: 3, time: 99, type: 'submitted', wrapper: W, message: 'eth/73', transactions: [TX] },
    { seq: 4, time: 9000, type: 'included', transactions: [TX], block: '0xe1' },
  ]);
  const bad = store.wrappers.get(BAD);
  const rows = phases(bad, bad.events.map(g => store.get(g)));
  assert.deepEqual(rows.map(r => [r.phase, r.own, r.outcome]), [['originate', true, 'ok'], ['validate', true, 'fail'], ['include', false, 'info']]);
  assert.equal(rows[1].summary, 'invalid: a wrapper dependency proof failed verification');
  assert.match(rows[2].summary, /^included in block 225 \(via a shared transaction/);
});

test('every feed event type has a phase entry, a spec link and a meaning', () => {
  for (const [type, [phase, happy, spec, meaning]] of Object.entries(stages.AUDIT)) {
    assert.ok(phase === null || stages.PHASE_INDEX[phase] !== undefined, type);
    assert.equal(typeof happy, 'boolean', type);
    assert.match(spec, /^https:\/\/eips\.ethereum\.org\/EIPS\/eip-(8437|8288)#/, type);
    assert.ok(meaning.length > 20, type);
  }
  assert.deepEqual(Object.keys(stages.AUDIT).sort(), Object.keys(stages.TYPES).sort());
});
