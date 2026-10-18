// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Where events come from: live nodes (polled by cursor), recorded captures (loaded or replayed) and a synthetic generator for
// load tests. Every source hands raw feed events to the same sink, sink(nodeKey, events).
(function (root) {
  'use strict';

  const PAGE = 2000;
  const IDLE_POLL_MS = 1000;
  const MAX_PAGES_PER_SECOND = 20;

  /** Polls debug_proofWrapperEvents on one node by cursor, catching up quickly and backing off when idle or behind. */
  class LivePoller {
    constructor(store, node, sink, { proxy, onStatus, busy }) {
      this.store = store; this.node = node; this.sink = sink; this.proxy = proxy;
      this.onStatus = onStatus || (() => {}); this.busy = busy || (() => false);
      this.stopped = false; this.pages = []; this.id = 0;
    }

    async rpc(method, params = []) {
      const url = this.proxy ? `rpc/${encodeURIComponent(this.node.key)}` : this.node.url;
      const response = await fetch(url, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ jsonrpc: '2.0', id: ++this.id, method, params }),
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const body = await response.json();
      if (body.error) throw new Error(body.error.message || JSON.stringify(body.error));
      return body.result;
    }

    async identify() {
      try {
        const info = await this.rpc('admin_nodeInfo');
        this.store.node(this.node.key, { enode: info.enode, client: info.name });
      } catch (error) { this.node.error = `admin_nodeInfo: ${error.message}`; }
      try {
        const peers = await this.rpc('admin_peers');
        this.node.adminPeers = new Set(peers.map(p => (p.enode || '').replace(/^enode:\/\//, '').split('@')[0].toLowerCase()));
      } catch { /* admin may be disabled; events still give peers */ }
    }

    async run() {
      await this.identify();
      let lastIdentify = Date.now();
      while (!this.stopped) {
        let delay = IDLE_POLL_MS;
        try {
          const started = performance.now();
          const page = await this.rpc('debug_proofWrapperEvents', [`0x${this.node.after.toString(16)}`, PAGE]);
          const latest = Number(page.latest);
          // A node restart resets its sequence: start again from its oldest retained event.
          if (latest < this.node.after) { this.node.after = 0; continue; }
          this.node.missed += Number(page.missed);
          this.node.clockOffset = Date.now() - page.time;
          this.node.status = 'live'; this.node.error = '';
          if (page.events.length) this.sink(this.node.key, page.events);
          this.pages.push(started);
          while (this.pages.length && this.pages[0] < started - 1000) this.pages.shift();
          // Catch up at once while pages are full, within a page budget and only while the UI keeps up.
          if (page.events.length === PAGE) delay = this.pages.length >= MAX_PAGES_PER_SECOND || this.busy() ? 250 : 0;
        } catch (error) {
          this.node.status = 'error'; this.node.error = error.message; delay = 3000;
        }
        this.onStatus(this.node);
        if (Date.now() - lastIdentify > 10000) { lastIdentify = Date.now(); this.identify(); }
        if (!this.stopped) await new Promise(r => setTimeout(r, document.hidden ? Math.max(delay, 2000) : delay));
      }
    }

    stop() { this.stopped = true; }
  }

  /** Releases timestamped raw events to the sink as a (possibly sped-up) clock reaches them. */
  class Scheduler {
    constructor(sink) { this.sink = sink; this.heap = []; this.timer = null; this.speed = 1; this.offset = 0; }
    push(nodeKey, event) { heapPush(this.heap, { t: event.time, nodeKey, event }); }
    start(origin, speed) {
      this.speed = speed; this.origin = origin; this.wall = Date.now();
      this.stop();
      this.timer = setInterval(() => this.tick(), 50);
    }
    now() { return this.origin + (Date.now() - this.wall) * this.speed; }
    tick(limit = 5000) {
      const now = this.now();
      const batch = new Map();
      let n = 0;
      while (this.heap.length && this.heap[0].t <= now && n++ < limit) {
        const { nodeKey, event } = heapPop(this.heap);
        let list = batch.get(nodeKey);
        if (!list) batch.set(nodeKey, list = []);
        list.push(event);
      }
      for (const [key, list] of batch) this.sink(key, list);
    }
    get pending() { return this.heap.length; }
    stop() { if (this.timer) clearInterval(this.timer); this.timer = null; }
  }

  function heapPush(h, x) { h.push(x); let i = h.length - 1; while (i > 0) { const p = (i - 1) >> 1; if (h[p].t <= h[i].t) break; [h[p], h[i]] = [h[i], h[p]]; i = p; } }
  function heapPop(h) {
    const top = h[0], last = h.pop();
    if (h.length) { h[0] = last; let i = 0; for (;;) { const l = 2 * i + 1, r = l + 1; let m = i; if (l < h.length && h[l].t < h[m].t) m = l; if (r < h.length && h[r].t < h[m].t) m = r; if (m === i) break; [h[m], h[i]] = [h[i], h[m]]; i = m; } }
    return top;
  }

  /** Replays a capture: events keep their relative timing, shifted to now and sped up by `speed`. */
  function replay(capture, store, sink, speed) {
    const scheduler = new Scheduler(sink);
    let first = Infinity;
    for (const list of Object.values(capture.events)) for (const e of list) if (e.time < first) first = e.time;
    const shift = Date.now() - first;
    for (const n of capture.nodes) store.node(n.key, { name: n.name, enode: n.enode, url: n.url });
    // Shift to wall-clock time, compressing gaps by the speed so the replay ends after (duration / speed).
    for (const [key, list] of Object.entries(capture.events))
      for (const e of list) scheduler.push(key, { ...e, time: first + (e.time - first) / speed + shift });
    scheduler.start(Date.now(), 1);
    return scheduler;
  }

  /** Loads a capture at once, through the sink so every view sees the events. */
  function load(capture, store, sink) {
    for (const n of capture.nodes) store.node(n.key, { name: n.name, enode: n.enode, url: n.url });
    for (const [key, list] of Object.entries(capture.events)) {
      for (let i = 0; i < list.length; i += 5000) sink(key, list.slice(i, i + 5000));
    }
  }

  const str = v => v === undefined || typeof v === 'string';

  /** Checks a capture's shape; captures are untrusted input, so node fields must be plain strings. */
  function validateCapture(capture) {
    if (!capture || capture.format !== 'eip8437-visualiser/1' || !Array.isArray(capture.nodes) || typeof capture.events !== 'object' || capture.events === null)
      throw new Error('Not an eip8437-visualiser/1 capture');
    for (const n of capture.nodes)
      if (!n || typeof n.key !== 'string' || !str(n.name) || !str(n.enode) || !str(n.url)) throw new Error('Capture node entries need string key, name, enode and url');
    for (const [key, list] of Object.entries(capture.events))
      if (!Array.isArray(list) || !list.every(e => e && typeof e.type === 'string' && Number.isFinite(Number(e.time)))) throw new Error(`Capture events for ${key} are malformed`);
    return capture;
  }

  // ---- Synthetic load ------------------------------------------------------------------------------------------------

  const hex = (rand, bytes = 32) => { let s = '0x'; for (let i = 0; i < bytes; i++) s += (rand() * 256 | 0).toString(16).padStart(2, '0'); return s; };
  function mulberry32(a) { return () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

  /**
   * Generates plausible multi-node event streams: each wrapper starts on one node and spreads hop by hop with announcements,
   * retrieval, envelope recovery (with occasional omissions and second sources), validation, admission and relay, plus
   * aggregation, proving, production and inclusion every slot. `rate` is wrappers per second.
   */
  class Synthetic {
    constructor(store, sink, { nodes = 6, rate = 5, slotMs = 12000, seed = 8437, busy = () => false } = {}) {
      this.store = store; this.rand = mulberry32(seed); this.rate = rate; this.slotMs = slotMs; this.busy = busy; this.throttled = 0;
      this.scheduler = new Scheduler(sink);
      this.keys = Array.from({ length: nodes }, (_, i) => `synthetic-${i + 1}`);
      this.ids = this.keys.map(() => hex(this.rand, 64));
      this.seq = this.keys.map(() => 0);
      this.keys.forEach((key, i) => store.node(key, { name: `sim-${i + 1}`, enode: `enode://${this.ids[i].slice(2)}@10.0.0.${i + 1}:30303` }));
      // A ring with chords, so wrappers need 1-3 hops.
      this.links = this.keys.map((_, i) => new Set([(i + 1) % nodes, (i + nodes - 1) % nodes]));
      for (let i = 0; i < nodes; i += 3) { const j = (i + Math.floor(nodes / 2)) % nodes; if (j !== i) { this.links[i].add(j); this.links[j].add(i); } }
      this.pending = [];
      this.block = 1;
    }

    emit(i, t, e) { this.scheduler.push(this.keys[i], { seq: `0x${(++this.seq[i]).toString(16)}`, time: t, ...e }); }
    peer(i) { return { peer: `[Session|sim|${this.keys[i]}]`, peerId: this.ids[i] }; }

    start() {
      const now = Date.now();
      this.keys.forEach((_, i) => this.links[i].forEach(j => this.emit(i, now, { type: 'peer-connected', ...this.peer(j), count: 0 })));
      this.scheduler.start(now, 1);
      this.next = now; this.nextSlot = now + this.slotMs; this.nextGauge = now;
      this.timer = setInterval(() => this.plan(), 100);
    }

    stop() { clearInterval(this.timer); this.scheduler.stop(); }

    plan() {
      const horizon = Date.now() + 200;
      // Backpressure: while the page is behind, skip this interval's wrappers instead of queueing them.
      const skip = this.busy() || this.scheduler.pending > 50000;
      if (skip) this.throttled++;
      while (this.next < horizon) {
        if (!skip) this.wrapper(this.next);
        this.next += -Math.log(1 - this.rand()) * 1000 / this.rate;
      }
      while (this.nextSlot < horizon) { this.slot(this.nextSlot); this.nextSlot += this.slotMs; }
      while (this.nextGauge < horizon) { this.gauges(this.nextGauge); this.nextGauge += 1000; }
    }

    wrapper(t0) {
      const r = this.rand, n = this.keys.length;
      const origin = r() * n | 0;
      const mode = r() < 0.55 ? 0 : 1;
      const txs = Array.from({ length: mode ? 1 + (r() * 6 | 0) : 1 }, () => hex(r));
      const deps = txs.map(() => `leanSPHINCS:${hex(r)}:${hex(r)}`);
      const hash = hex(r);
      const length = mode ? 250000 + (r() * 40000 | 0) : 6355;
      const invalidAt = r() < 0.02 ? 1 + (r() * (n - 1) | 0) : -1; // a node that serves an invalid copy
      const body = { wrapper: hash, mode, length, transactions: txs, dependencies: deps };
      if (mode && r() < 0.5) {
        const ms = 300 + r() * 2500;
        this.emit(origin, t0, { type: 'proof-step', message: r() < 0.6 ? 'leaf' : 'merge', result: 'proven', durationMs: ms, length: length - 147, count: deps.length, detail: 'background' });
        this.emit(origin, t0 + 1, { type: 'aggregated', result: r() < 0.4 ? 'reaggregated' : 'aggregated', durationMs: ms + 20, ...body });
      } else {
        this.emit(origin, t0, { type: 'submitted', message: 'local', ...body });
        this.emit(origin, t0 + 3, { type: 'proof-verified', wrapper: hash, mode, result: 'valid', durationMs: mode ? 40 + r() * 80 : 3 + r() * 20 });
        txs.forEach(tx => this.emit(origin, t0 + 5, { type: 'admitted', wrapper: hash, transactions: [tx], result: 'Accepted' }));
      }
      const offered = new Map([[origin, t0 + 6]]);
      this.emit(origin, t0 + 6, { type: 'offered', result: 'local aggregate', count: txs.length, ...body });
      const queue = [origin];
      while (queue.length) {
        const i = queue.shift();
        const ti = offered.get(i);
        for (const j of this.links[i]) {
          const ta = ti + 2 + r() * 25;
          this.emit(i, ti + 1, { type: 'announced', ...this.peer(j), wrapper: hash, message: 'NewProofWrapperHashes', length, count: 1 });
          if (offered.has(j) && offered.get(j) <= ta) {
            this.emit(j, ta, { type: 'announcement-received', ...this.peer(i), wrapper: hash, message: 'NewProofWrapperHashes', result: 'already held', length });
            continue;
          }
          if (offered.has(j)) continue;
          const tj = this.hop(i, j, ta, body, invalidAt === j);
          if (tj === undefined) continue;
          offered.set(j, tj);
          queue.push(j);
        }
      }
      if (!txs.length) return;
      this.pending.push({ txs, offered });
    }

    hop(i, j, ta, body, invalid) {
      const r = this.rand, hash = body.wrapper, id = this.seq[j] + 1;
      this.emit(j, ta, { type: 'announcement-received', ...this.peer(i), wrapper: hash, message: 'NewProofWrapperHashes', result: 'queued', length: body.length });
      const tq = ta + 0.5;
      this.emit(j, tq, { type: 'wrapper-requested', ...this.peer(i), wrapper: hash, message: 'GetProofWrappers', requestId: id, length: body.length, count: 1 });
      const transfer = body.length / 50000 + 2 + r() * 20;
      this.emit(i, tq + 3, { type: 'served', ...this.peer(j), wrapper: hash, message: 'ProofWrappers', requestId: id, result: 'served', length: body.length });
      const tr = tq + transfer;
      this.emit(j, tr, { type: 'wrapper-received', ...this.peer(i), wrapper: hash, message: 'ProofWrappers', requestId: id, result: 'matched hash and announced length; queued for validation', length: body.length });
      const missing = body.transactions.length;
      this.emit(j, tr + 0.5, { type: 'validation-started', ...this.peer(i), ...body, count: missing });
      let t = tr + 1, rounds = 0;
      const omit = r() < 0.08 && missing > 1;
      const sources = omit ? [i, [...this.links[j]].find(k => k !== i) ?? i] : [i];
      for (const src of sources) {
        rounds++;
        const rid = this.seq[j] + 100;
        this.emit(j, t, { type: 'envelope-requested', ...this.peer(src), wrapper: hash, message: 'GetPooledTransactions', requestId: rid, count: rounds, transactions: body.transactions, detail: rounds > 1 ? 'asking another announcer' : 'asking the peer that served the wrapper' });
        const rt = 1 + r() * 30;
        const returned = omit && rounds === 1 ? missing - 1 : missing;
        this.emit(j, t + rt, { type: 'envelope-response', ...this.peer(src), wrapper: hash, message: 'PooledTransactions', requestId: rid, result: `returned ${returned} of ${missing}`, detail: omit && rounds === 1 ? '1 omitted before the last returned: unavailable here, try another announcer' : undefined, count: returned, durationMs: rt });
        if (rounds === 1) this.emit(src, t + 1, { type: 'envelope-served', message: 'PooledTransactions', transactions: [body.transactions[0]], detail: 'served from the wrapper-envelope cache' });
        t += rt;
      }
      this.emit(j, t, { type: 'envelope-recovery', ...this.peer(i), wrapper: hash, result: 'complete', detail: `recovered ${missing} of ${missing} envelopes from ${sources.length} sources`, count: rounds, durationMs: t - tr - 1 });
      const cached = r() < 0.1;
      const vms = cached ? 0.2 : body.mode ? 40 + r() * 120 : 3 + r() * 25;
      this.emit(j, tr + 2, cached
        ? { type: 'claims-checked', wrapper: hash, result: invalid ? 'invalid' : 'valid', detail: 'verdict cache' }
        : { type: 'claims-checked', wrapper: hash, mode: body.mode, result: invalid ? 'invalid' : 'valid', durationMs: vms });
      const tv = Math.max(t, tr + 2 + vms) + 1;
      if (invalid) {
        this.emit(j, tv, { type: 'verdict', ...this.peer(i), wrapper: hash, result: 'Invalid', detail: 'a wrapper dependency proof failed verification', durationMs: tv - ta });
        this.emit(j, tv, { type: 'violation', ...this.peer(i), result: 'disconnected', detail: `served invalid wrapper ${hash.slice(0, 10)}` });
        this.emit(j, tv, { type: 'peer-disconnected', ...this.peer(i), count: 0 });
        this.emit(j, tv + 2000, { type: 'peer-connected', ...this.peer(i), count: 0 });
        return undefined;
      }
      this.emit(j, tv, { type: 'proof-verified', wrapper: hash, mode: body.mode, result: 'valid', detail: 'claimed proofs already checked; transaction list verified', durationMs: 0.5 + r() * 2 });
      this.emit(j, tv + 1, { type: 'offered', result: 'validated', count: missing, ...body });
      body.transactions.forEach(tx => this.emit(j, tv + 2, { type: 'admitted', wrapper: hash, transactions: [tx], result: 'Accepted' }));
      this.emit(j, tv + 2, { type: 'verdict', ...this.peer(i), wrapper: hash, result: 'Valid', durationMs: tv + 2 - ta });
      return tv + 1;
    }

    slot(t) {
      const r = this.rand, producer = r() * this.keys.length | 0, block = this.block++;
      const ready = this.pending.filter(p => [...p.offered.values()].every(o => o < t - 1000)).slice(0, 200);
      this.pending = this.pending.filter(p => !ready.includes(p));
      const deps = ready.reduce((a, p) => a + p.txs.length, 0);
      const missed = deps > 0 && r() < 0.15;
      this.emit(producer, t - 4000, { type: 'production-pass', block, count: deps, result: missed ? 'not-ready' : deps ? 'prepared' : 'reused', durationMs: missed ? 1 : 2 + r() * 10, length: deps ? 270000 : 0, detail: missed ? `proof not ready by the deadline: rebuilding from ${deps >> 1} proven dependencies` : undefined });
      if (missed) {
        this.emit(producer, t - 3990, { type: 'production-scheduled', block, count: deps });
        this.emit(producer, t + 6000 + r() * 6000, { type: 'production-proof', block, count: deps, result: 'proven', durationMs: 6000 + r() * 6000, length: 270000 });
      }
      for (const p of ready.slice(0, missed ? ready.length >> 1 : ready.length))
        this.keys.forEach((_, i) => p.txs.forEach(tx => this.emit(i, t + 50 + r() * 100, { type: 'included', transactions: [tx], block })));
      if (missed) this.pending.push(...ready.slice(ready.length >> 1));
      this.keys.forEach((_, i) => {
        this.emit(i, t + 200, { type: 'recursive-verify', result: r() < 0.6 ? 'cached' : 'cold', durationMs: 20 + r() * 40, length: 270000 });
        if (r() < 0.3) this.emit(i, t + 300, { type: 'proof-step', message: r() < 0.5 ? 'merge' : 'fold', result: r() < 0.3 ? 'cache-hit' : 'proven', durationMs: 800 + r() * 4000, length: 268000, count: 2 });
      });
    }

    gauges(t) {
      this.keys.forEach((_, i) => {
        const p = this.pending.length;
        this.emit(i, t, { type: 'gauges', detail: `peers=${this.links[i].size} pending=${(this.rand() * this.rate) | 0} validation=${(this.rand() * 3) | 0} workers=${(this.rand() * 4) | 0} stored=${Math.min(512, p)} storedBytes=${p * 140000} chargedBytes=${(this.rand() * 4e6) | 0} tombstones=0 pool=${p * 2}` });
      });
    }
  }

  /** Accepts name=url lines, bare URLs, `kurtosis enclave inspect` or `kurtosis port print` output. */
  function parseNodes(text, existing = 0) {
    const found = [];
    let service = '';
    for (const line of text.split('\n')) {
      const svc = line.match(/^[0-9a-f]{12}\s+(\S+)/);
      if (svc) service = svc[1];
      const named = line.match(/^\s*([\w.-]+)\s*=\s*(\S+)/);
      if (named) { found.push({ name: named[1], url: normalize(named[2]) }); continue; }
      const rpc = line.match(/(?<![\w-])rpc:\s*\d+\/tcp\s*->\s*(\S+)/);
      if (rpc) { found.push({ name: service.replace(/-nethermind.*$/, '') || `node-${found.length + 1}`, url: normalize(rpc[1]) }); continue; }
      const bare = line.match(/^\s*((?:https?:\/\/)?[\w.-]+:\d+)\s*$/);
      if (bare) found.push({ name: `node-${existing + found.length + 1}`, url: normalize(bare[1]) });
    }
    return found;
  }
  const normalize = url => /^https?:\/\//.test(url) ? url : `http://${url}`;

  const api = { parseNodes, LivePoller, Scheduler, replay, load, validateCapture, Synthetic };
  root.PWV = Object.assign(root.PWV || {}, { sources: api });
  if (typeof module !== 'undefined') module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
