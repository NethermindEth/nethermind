// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Bounded client-side store of debug_proofWrapperEvents from several nodes, indexed per wrapper, plus the aggregates the
// dashboard draws. Everything here is plain data so it also runs under node for tests.
(function (root) {
  'use strict';
  const stages = root.PWV?.stages || (typeof require !== 'undefined' ? require('./stages.js') : null);

  const num = v => v === undefined || v === null ? undefined : typeof v === 'string' && v.startsWith('0x') ? parseInt(v, 16) : Number(v);
  // Feed fields are strings or string arrays; anything else (from a hand-edited capture) is dropped.
  const text = v => typeof v === 'string' ? v : undefined;
  const texts = (v, fold = x => x) => Array.isArray(v) && v.every(x => typeof x === 'string') ? v.map(fold) : undefined;
  const hashes = v => texts(v, x => x.toLowerCase());
  const lower = v => v ? String(v).toLowerCase().replace(/^0x/, '') : '';

  // Types whose `transactions` name the wrapper's own transaction list.
  const LISTS_TRANSACTIONS = new Set(['submitted', 'validation-started', 'offered', 'aggregated']);
  // Transaction-level events linked to every wrapper that lists the transaction.
  const TX_EVENTS = new Set(['included', 'upgrade-evicted', 'pool-evicted', 'envelope-served', 'admitted']);
  const MAX_WRAPPER_EVENTS = 400;
  const MAX_UNLINKED = 5000;
  const MAX_GAUGE_POINTS = 3600;

  function percentile(sorted, p) {
    if (!sorted.length) return NaN;
    const i = Math.min(sorted.length - 1, Math.max(0, Math.ceil(p / 100 * sorted.length) - 1));
    return sorted[i];
  }

  function summary(values) {
    const sorted = Float64Array.from(values).sort();
    return { n: sorted.length, p50: percentile(sorted, 50), p95: percentile(sorted, 95), p99: percentile(sorted, 99), max: sorted.length ? sorted[sorted.length - 1] : NaN, sorted };
  }

  class Store {
    constructor({ capacity = 200000, maxWrappers = 20000 } = {}) {
      this.capacity = capacity;
      this.maxWrappers = maxWrappers;
      this.clear();
    }

    clear() {
      this.ring = new Array(this.capacity);
      this.head = 0; // next write position
      this.size = 0;
      this.nextGid = 1; // global id of the next event; ids of retained events are [nextGid - size, nextGid)
      this.nodes = new Map();
      this.wrappers = new Map();
      this.txToWrappers = new Map();
      this.unlinked = new Map(); // tx hash -> gids of transaction events seen before any wrapper listing it
      this.peerIds = new Map(); // node public key -> node key
      this.version = 0;
      this.evicted = 0;
      this.duplicates = 0;
    }

    get minGid() { return this.nextGid - this.size; }

    get(gid) {
      if (gid < this.minGid || gid >= this.nextGid) return undefined;
      return this.ring[(this.head - (this.nextGid - gid) + this.capacity) % this.capacity];
    }

    *events() {
      for (let gid = this.minGid; gid < this.nextGid; gid++) yield this.get(gid);
    }

    node(key, info = {}) {
      let node = this.nodes.get(key);
      if (!node) {
        node = { key, name: info.name || key, url: info.url, index: this.nodes.size, id: '', after: 0, epoch: 0, missed: 0, latest: 0, peers: new Set(), links: new Set(), gauges: new Map(), clockOffset: 0, status: 'idle', error: '' };
        this.nodes.set(key, node);
      }
      Object.assign(node, Object.fromEntries(Object.entries(info).filter(([, v]) => v !== undefined)));
      if (info.enode) node.id = lower(info.enode.replace(/^enode:\/\//, '').split('@')[0]);
      if (node.id) this.peerIds.set(node.id, key);
      return node;
    }

    peerNode(peerId) {
      const id = lower(peerId);
      return id ? this.peerIds.get(id) : undefined;
    }

    /**
     * Adds raw feed events from one node; returns the normalized events in order.
     * @param epoch Increments when the node's feed restarted from sequence 1; batches from an earlier epoch are dropped.
     * Within an epoch an event at or below the node's last ingested sequence is a duplicate and skipped.
     */
    ingest(nodeKey, raw, epoch) {
      const node = this.node(nodeKey);
      if (epoch !== undefined) {
        if (epoch < node.epoch) return [];
        if (epoch > node.epoch) { node.epoch = epoch; node.after = 0; }
      }
      const added = [];
      for (const r of raw) {
        const seq = num(r.seq);
        if (seq !== undefined && seq <= node.after) { this.duplicates++; continue; }
        const e = {
          gid: this.nextGid++, node: nodeKey, seq, t: num(r.time), type: String(r.type),
          wrapper: text(r.wrapper)?.toLowerCase(), peer: text(r.peer), peerId: text(r.peerId), message: text(r.message), requestId: num(r.requestId),
          result: text(r.result), detail: text(r.detail), mode: num(r.mode), length: num(r.length), transactions: hashes(r.transactions),
          dependencies: texts(r.dependencies), count: num(r.count), durationMs: num(r.durationMs), block: num(r.block), blockHash: text(r.blockHash),
        };
        if (e.peerId) e.peerNode = this.peerNode(e.peerId);
        const d = stages.describe(e);
        e.stage = d.stage;
        this.push(e);
        if (e.seq > node.after) node.after = e.seq;
        if (e.t > node.latest) node.latest = e.t;
        this.index(node, e);
        added.push(e);
      }
      if (added.length) this.version++;
      return added;
    }

    push(e) {
      if (this.size === this.capacity) this.evicted++;
      else this.size++;
      this.ring[this.head] = e;
      this.head = (this.head + 1) % this.capacity;
    }

    index(node, e) {
      if (e.type === 'gauges') return this.gauge(node, e);
      if (e.peerId) node.links.add(lower(e.peerId));
      if (e.type === 'peer-connected' && e.peerId) node.peers.add(lower(e.peerId));
      if (e.type === 'peer-disconnected' && e.peerId) node.peers.delete(lower(e.peerId));
      if (e.wrapper) {
        const w = this.wrapper(e.wrapper, e.t);
        this.attach(w, e);
        if (e.transactions && LISTS_TRANSACTIONS.has(e.type)) this.listTransactions(w, e.transactions);
      } else if (e.transactions && TX_EVENTS.has(e.type)) {
        for (const tx of e.transactions) {
          const owners = this.txToWrappers.get(tx);
          if (owners) for (const hash of owners) this.attach(this.wrappers.get(hash), e);
          else this.remember(tx, e.gid);
        }
      }
    }

    remember(tx, gid) {
      let gids = this.unlinked.get(tx);
      if (!gids) {
        if (this.unlinked.size >= MAX_UNLINKED) this.unlinked.delete(this.unlinked.keys().next().value);
        this.unlinked.set(tx, gids = []);
      }
      gids.push(gid);
    }

    wrapper(hash, t) {
      let w = this.wrappers.get(hash);
      if (!w) {
        if (this.wrappers.size >= this.maxWrappers) this.forget(this.wrappers.keys().next().value);
        w = { hash, first: t, last: t, mode: undefined, length: undefined, txs: [], deps: [], perNode: new Map(), events: [], eventSet: new Set(),
          included: undefined, txIncluded: undefined, outcome: 'pending' };
        this.wrappers.set(hash, w);
      }
      return w;
    }

    forget(hash) {
      const w = this.wrappers.get(hash);
      this.wrappers.delete(hash);
      for (const tx of w?.txs || []) {
        const owners = this.txToWrappers.get(tx);
        owners?.delete(hash);
        if (owners && !owners.size) this.txToWrappers.delete(tx);
      }
    }

    listTransactions(w, txs) {
      if (w.txs.length) return;
      w.txs = txs;
      for (const tx of txs) {
        let owners = this.txToWrappers.get(tx);
        if (!owners) this.txToWrappers.set(tx, owners = new Set());
        owners.add(w.hash);
        const pending = this.unlinked.get(tx);
        if (pending) {
          this.unlinked.delete(tx);
          for (const gid of pending) { const e = this.get(gid); if (e) this.attach(w, e); }
        }
      }
    }

    /**
     * Adds an event to a wrapper's history. Events that name another wrapper or none, linked only because they concern a
     * transaction this wrapper also lists (inclusion, eviction, serving its envelope), are kept apart from the wrapper's own
     * steps: a rejected copy of a transaction must not look included when a valid wrapper carried the same transaction.
     */
    attach(w, e) {
      if (!w || w.eventSet.has(e.gid)) return;
      w.eventSet.add(e.gid);
      if (e.t < w.first) w.first = e.t;
      if (e.t > w.last) w.last = e.t;
      if (e.mode !== undefined && w.mode === undefined) w.mode = e.mode;
      if (e.length && !w.length && e.type !== 'wrapper-requested') w.length = e.length;
      if (e.dependencies && !w.deps.length) w.deps = e.dependencies;
      if (w.events.length < MAX_WRAPPER_EVENTS) w.events.push(e.gid);
      let n = w.perNode.get(e.node);
      if (!n) w.perNode.set(e.node, n = { first: e.t, stages: {}, txStages: {}, rounds: 0, omitted: 0 });
      const own = e.wrapper === w.hash;
      const stages = own ? n.stages : n.txStages;
      if (!(e.stage in stages)) stages[e.stage] = e.t;
      if (!own) {
        if (e.type === 'included' && w.txIncluded === undefined) w.txIncluded = { block: e.block, t: e.t };
        w.outcome = outcomeOf(w);
        return;
      }
      switch (e.type) {
        case 'announced':
          if (e.peerId) ((n.announcedTo ||= {})[lower(e.peerId)] ||= []).push(e.t);
          break;
        case 'announcement-received':
          if (n.announcedAt === undefined) { n.announcedAt = e.t; n.fromId = e.peerId; }
          break;
        case 'wrapper-received': if (n.receivedAt === undefined && (e.result || '').startsWith('matched')) n.receivedAt = e.t; break;
        case 'envelope-requested': n.rounds++; break;
        case 'envelope-response': if (/omitted/.test(e.detail || '')) n.omitted++; break;
        case 'envelope-recovery': n.recoveryMs = e.durationMs; break;
        case 'verdict': n.verdict = e.result; n.verdictAt = e.t; break;
        case 'offered': if (n.offeredAt === undefined) { n.offeredAt = e.t; n.local = e.result === 'local aggregate'; } break;
        case 'submitted': if (n.offeredAt === undefined && e.message !== 'eth/73') n.submittedAt = e.t; break;
      }
      w.outcome = outcomeOf(w);
    }

    gauge(node, e) {
      for (const pair of (e.detail || '').split(' ')) {
        const [k, v] = pair.split('=');
        if (!k || v === undefined) continue;
        let series = node.gauges.get(k);
        if (!series) node.gauges.set(k, series = []);
        series.push([e.t, Number(v)]);
        if (series.length > MAX_GAUGE_POINTS) series.splice(0, series.length - MAX_GAUGE_POINTS);
      }
    }

    toJSON() {
      const byNode = {};
      for (const e of this.events()) (byNode[e.node] ||= []).push(raw(e));
      return {
        format: 'eip8437-visualiser/1',
        exported: Date.now(),
        nodes: [...this.nodes.values()].map(n => ({ key: n.key, name: n.name, enode: n.enode, url: n.url })),
        events: byNode,
      };
    }
  }

  function raw(e) {
    const r = { seq: e.seq, time: e.t, type: e.type };
    for (const k of ['wrapper', 'peer', 'peerId', 'message', 'requestId', 'result', 'detail', 'mode', 'length', 'transactions', 'dependencies', 'count', 'durationMs', 'block', 'blockHash'])
      if (e[k] !== undefined) r[k] = e[k];
    return r;
  }

  /**
   * A wrapper's outcome from its own steps. Its transactions' inclusion makes it `included` only if some node validated
   * or offered this wrapper, or never judged it; a wrapper every node rejected stays `rejected` even when a valid wrapper
   * carrying the same transaction got it included (`w.included` then stays unset and `w.txIncluded` records the block).
   */
  function outcomeOf(w) {
    let validated = false, rejected = false, dropped = false, txDropped = false;
    for (const n of w.perNode.values()) {
      if ('validated' in n.stages || 'relayed' in n.stages || 'admitted' in n.stages) validated = true;
      if ('rejected' in n.stages) rejected = true;
      if ('dropped' in n.stages) dropped = true;
      if ('dropped' in n.txStages) txDropped = true;
    }
    w.included = w.txIncluded && (validated || !rejected) ? w.txIncluded : undefined;
    if (w.included) return 'included';
    if (validated) return dropped || txDropped ? 'dropped' : 'validated';
    if (rejected) return 'rejected';
    return dropped || txDropped ? 'dropped' : 'pending';
  }

  /**
   * Collapses a wrapper's time-ordered events into steps: consecutive events on the same node with the same type, result
   * and message (announcements from several peers, serving several requesters, per-transaction admission) become one step
   * with a count; the members keep their own times and peers for the expanded view.
   * @returns {{e: object, members: object[], own: boolean}[]} steps in time order of their first event.
   */
  function groupSteps(w, events) {
    const steps = [], last = new Map();
    for (const e of events) {
      const own = e.wrapper === w.hash;
      const key = `${e.type}|${e.result ?? ''}|${e.message ?? ''}|${own}`;
      const previous = last.get(e.node);
      if (previous && previous.key === key) { previous.step.members.push(e); continue; }
      const step = { e, members: [e], own };
      steps.push(step);
      last.set(e.node, { key, step });
    }
    return steps;
  }

  /** Other wrappers listing one of `w`'s transactions that some node validated: what carried a rejected wrapper's
   * transaction instead. */
  function carriers(store, w) {
    const found = new Set();
    for (const tx of w.txs)
      for (const hash of store.txToWrappers.get(tx) || [])
        if (hash !== w.hash && ['validated', 'included', 'dropped'].includes(store.wrappers.get(hash)?.outcome)) found.add(hash);
    return [...found];
  }

  /**
   * When a hop started on the sending node: its last announcement to the receiver before the receiver heard of the wrapper,
   * else its own offer. A node that reconnects after a restart is re-announced wrappers offered long before, so measuring
   * from the original offer would count the outage as propagation latency.
   */
  function hopStart(src, receiverId, receivedAt) {
    let start;
    for (const t of src.announcedTo?.[receiverId] || []) if (t <= receivedAt && (start === undefined || t > start)) start = t;
    return start ?? src.offeredAt;
  }

  /** Whether a wrapper passes the node / mode / outcome filters. */
  function wrapperMatches(w, f) {
    if (!w) return !f.mode && !f.outcome;
    if (f.mode !== '' && f.mode !== undefined && String(w.mode) !== String(f.mode)) return false;
    if (f.outcome && w.outcome !== f.outcome) return false;
    if (f.node && !w.perNode.has(f.node)) return false;
    return true;
  }

  /** Whether an event passes every filter, text included. */
  function eventMatches(store, e, f) {
    if (f.node && e.node !== f.node) return false;
    if (f.stage && e.stage !== f.stage) return false;
    if ((f.mode !== '' && f.mode !== undefined) || f.outcome) {
      const w = e.wrapper ? store.wrappers.get(e.wrapper) : undefined;
      if (!w) return false;
      if (f.mode !== '' && f.mode !== undefined && String(w.mode) !== String(f.mode)) return false;
      if (f.outcome && w.outcome !== f.outcome) return false;
    }
    if (f.text) {
      const t = f.text.toLowerCase();
      const hay = `${e.type} ${e.wrapper || ''} ${e.peer || ''} ${e.result || ''} ${e.detail || ''} ${(e.transactions || []).join(' ')}`.toLowerCase();
      if (!hay.includes(t)) return false;
    }
    return true;
  }

  /**
   * Everything the overview draws, from one pass over the retained events and wrappers.
   * @param {Store} store
   * @param {{node?: string, mode?: string, outcome?: string}} filter
   * @param {{now: number, windowMs: number, binMs: number, top?: number}} view
   */
  function aggregate(store, filter, view) {
    const { start, bins, binMs } = alignedWindow(view.now, view.windowMs, view.binMs);
    const stageIds = stages.STAGES.map(s => s.id);
    const heat = stageIds.map(() => new Uint32Array(bins));
    const proving = { leaf: [], merge: [], fold: [], discard: [], cacheHits: 0, invalid: 0 };
    const verify = { cold: [], cached: 0, claims: [], recursiveCold: [], recursiveCached: 0 };
    const aggregation = { aggregated: [], reaggregated: [], 'reused cached proof': [] };
    const production = [];
    const productionProofs = [];
    const counts = { events: 0, envelopeRequests: 0, envelopesRecovered: 0, violations: 0, omissions: 0, rejections: 0, disconnects: 0 };
    const series = { hop: [], recovery: [], verify: [], fetch: [], step: [] }; // [t, value]
    const wrapperOf = hash => hash ? store.wrappers.get(hash) : undefined;
    const filteringWrappers = (filter.mode !== '' && filter.mode !== undefined) || !!filter.outcome;

    for (const e of store.events()) {
      if (filter.node && e.node !== filter.node) continue;
      const w = wrapperOf(e.wrapper);
      if (filteringWrappers && e.wrapper && !wrapperMatches(w, { ...filter, node: '' })) continue;
      if (filteringWrappers && !e.wrapper && e.stage !== 'proving') continue;
      counts.events++;
      const idx = stages.STAGE_INDEX[e.stage];
      if (idx !== undefined && e.t >= start) {
        const b = Math.floor((e.t - start) / binMs);
        if (b >= 0 && b < bins) heat[idx][b]++;
      }
      switch (e.type) {
        case 'proof-step':
          if (e.result === 'cache-hit') proving.cacheHits++;
          else if (e.result === 'invalid') proving.invalid++;
          else if (proving[e.message]) { proving[e.message].push(e.durationMs); series.step.push([e.t, e.durationMs]); }
          break;
        case 'proof-verified': if (e.result === 'valid' || e.result === 'invalid') { verify.cold.push(e.durationMs); series.verify.push([e.t, e.durationMs]); } break;
        case 'verdict-cached': verify.cached++; break;
        case 'claims-checked': if (e.durationMs !== undefined) verify.claims.push(e.durationMs); else verify.cached++; break;
        case 'recursive-verify': if (e.result === 'cached') verify.recursiveCached++; else verify.recursiveCold.push(e.durationMs); break;
        case 'aggregated': (aggregation[e.result] ||= []).push(e.durationMs); break;
        case 'production-pass': production.push({ node: e.node, block: e.block, result: e.result, ms: e.durationMs, deps: e.count, bytes: e.length, t: e.t, detail: e.detail }); break;
        case 'production-proof': productionProofs.push({ node: e.node, block: e.block, result: e.result, ms: e.durationMs, deps: e.count, bytes: e.length, t: e.t }); break;
        case 'envelope-requested': counts.envelopeRequests++; break;
        case 'envelope-response': counts.envelopesRecovered += e.count || 0; if (/omitted/.test(e.detail || '')) counts.omissions++; break;
        case 'violation': counts.violations++; break;
        case 'peer-disconnected': counts.disconnects++; break;
        case 'verdict': if (e.result !== 'Valid') counts.rejections++; break;
      }
    }

    const hop = [], local = [], fetch = [], recovery = [], rounds = [], e2e = [], sizes = { 0: [], 1: [] };
    const outcomes = { pending: 0, validated: 0, rejected: 0, dropped: 0, included: 0 };
    let wrappers = 0;
    for (const w of store.wrappers.values()) {
      if (!wrapperMatches(w, filter)) continue;
      wrappers++;
      outcomes[w.outcome]++;
      if (w.length && (w.mode === 0 || w.mode === 1)) sizes[w.mode].push(w.length);
      let lastOffer = -Infinity, origin = Infinity;
      for (const [key, n] of w.perNode) {
        if (filter.node && key !== filter.node) continue;
        const begin = n.submittedAt ?? (n.local ? n.offeredAt : undefined);
        if (begin !== undefined && begin < origin) origin = begin;
        if (n.offeredAt !== undefined && n.offeredAt > lastOffer) lastOffer = n.offeredAt;
        if (n.offeredAt !== undefined && n.announcedAt !== undefined) {
          local.push(n.offeredAt - n.announcedAt);
          const from = store.peerNode(n.fromId);
          const src = from ? w.perNode.get(from) : undefined;
          const start = src && hopStart(src, store.nodes.get(key)?.id, n.announcedAt);
          if (start !== undefined && start <= n.offeredAt) {
            hop.push({ ms: n.offeredAt - start, w: w.hash, from, to: key });
            series.hop.push([n.offeredAt, n.offeredAt - start]);
          }
        }
        if (n.announcedAt !== undefined && n.receivedAt !== undefined) { fetch.push(n.receivedAt - n.announcedAt); series.fetch.push([n.receivedAt, n.receivedAt - n.announcedAt]); }
        if (n.recoveryMs !== undefined) { recovery.push(n.recoveryMs); series.recovery.push([n.offeredAt ?? n.first, n.recoveryMs]); }
        if (n.rounds) rounds.push(n.rounds);
      }
      if (origin !== Infinity && lastOffer > origin) e2e.push({ ms: lastOffer - origin, w: w.hash });
    }
    e2e.sort((a, b) => b.ms - a.ms);

    return {
      start, bins, binMs, heat, stageIds, counts, wrappers, outcomes, sizes,
      hop: summary(hop.map(h => h.ms)), hops: hop, local: summary(local), fetch: summary(fetch), recovery: summary(recovery), rounds,
      e2e: summary(e2e.map(x => x.ms)), slowest: e2e.slice(0, view.top ?? 10),
      proving, verify, aggregation, production, productionProofs, series,
    };
  }

  /**
   * A window of whole bins ending at the bin that contains `now`. Bin edges are multiples of `binMs`, so a redraw a moment
   * later puts every sample in the same bin as before instead of shifting all edges.
   */
  function alignedWindow(now, windowMs, binMs) {
    const bins = Math.max(1, Math.ceil(windowMs / binMs));
    const end = (Math.floor(now / binMs) + 1) * binMs;
    return { start: end - bins * binMs, bins, binMs, end };
  }

  /**
   * Points ready to draw: finite values inside [t0, t1], sorted by time, and at most about `maxPoints` of them. Denser
   * series keep each slice's first, minimum and maximum points, in time order, so spikes survive decimation.
   */
  function prepareSeries(points, t0, t1, maxPoints = 1000) {
    const kept = points.filter(p => p && Number.isFinite(p[0]) && Number.isFinite(p[1]) && p[0] >= t0 && p[0] <= t1);
    kept.sort((a, b) => a[0] - b[0]);
    if (kept.length <= maxPoints) return kept;
    const slices = Math.max(1, Math.floor(maxPoints / 3)), width = (t1 - t0) / slices, out = [];
    for (let i = 0; i < kept.length;) {
      const slice = Math.min(slices - 1, Math.floor((kept[i][0] - t0) / width));
      let first = kept[i], lo = kept[i], hi = kept[i];
      for (i++; i < kept.length && Math.min(slices - 1, Math.floor((kept[i][0] - t0) / width)) === slice; i++) {
        if (kept[i][1] < lo[1]) lo = kept[i];
        if (kept[i][1] > hi[1]) hi = kept[i];
      }
      for (const p of [...new Set([first, lo, hi])].sort((a, b) => a[0] - b[0])) out.push(p);
    }
    return out;
  }

  /** Indexes `i` where the step from point i-1 to point i spans more than `maxGap`: no samples arrived in between. */
  function gaps(points, maxGap) {
    const found = [];
    for (let i = 1; i < points.length; i++) if (points[i][0] - points[i - 1][0] > maxGap) found.push(i);
    return found;
  }

  /** The point of a time-sorted series nearest to `t`. */
  function nearest(points, t) {
    let lo = 0, hi = points.length - 1;
    if (hi < 0) return undefined;
    while (lo < hi) { const mid = (lo + hi) >> 1; if (points[mid][0] < t) lo = mid + 1; else hi = mid; }
    return lo > 0 && t - points[lo - 1][0] < points[lo][0] - t ? points[lo - 1] : points[lo];
  }

  /** p50 and p95 of [t, value] points per bin. */
  function percentileSeries(points, start, bins, binMs) {
    const buckets = Array.from({ length: bins }, () => []);
    for (const [t, v] of points) {
      const b = Math.floor((t - start) / binMs);
      if (b >= 0 && b < bins && Number.isFinite(v)) buckets[b].push(v);
    }
    return buckets.map(values => {
      if (!values.length) return null;
      values.sort((a, b) => a - b);
      return { n: values.length, p50: percentile(values, 50), p95: percentile(values, 95) };
    });
  }

  const api = { Store, carriers, groupSteps, alignedWindow, prepareSeries, gaps, nearest, aggregate, percentileSeries, eventMatches, wrapperMatches, summary, percentile, num };
  root.PWV = Object.assign(root.PWV || {}, { model: api });
  if (typeof module !== 'undefined') module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
