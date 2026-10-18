// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

(function () {
  'use strict';
  const { stages, model, charts, network, sources } = window.PWV;
  const { esc, fmtMs, fmtBytes, fmtCount } = charts;
  const $ = id => document.getElementById(id);

  const store = new model.Store({ capacity: 200000, maxWrappers: 20000 });
  const state = {
    filter: { node: '', mode: '', stage: '', outcome: '', text: '' },
    windowMs: 900000, tab: 'overview', paused: false, wrapper: null,
    pollers: new Map(), schedulers: [], proxy: false,
    agg: null, aggAt: 0, aggCost: 0, aggVersion: -1, aggKey: '',
    log: [], logKey: '', logVersion: -1, logAt: 0,
    frames: 0, fps: 0, fpsAt: performance.now(), ingested: 0, rate: 0, rateAt: performance.now(),
  };
  const view = new network.NetworkView($('network'), store, { onNode: key => setFilter('node', state.filter.node === key ? '' : key) });

  const short = h => h ? `${h.slice(0, 10)}…${h.slice(-4)}` : '';
  const nodeName = key => store.nodes.get(key)?.name || (key?.startsWith('ext:') ? `peer ${key.slice(4, 12)}` : key || '');
  const peerName = e => { const k = e.peerNode || store.peerNode(e.peerId); return k ? nodeName(k) : e.peerId ? `peer ${e.peerId.slice(2, 10)}` : ''; };
  const time = t => { const d = new Date(t); return `${d.toLocaleTimeString([], { hour12: false })}.${String(d.getMilliseconds()).padStart(3, '0')}`; };
  const OUTCOME_CLASS = { included: 'good', validated: 'good', pending: 'info', rejected: 'bad', dropped: 'warn' };
  const pill = outcome => `<span class="pill ${OUTCOME_CLASS[outcome] || 'info'}">${esc(outcome)}</span>`;
  const GROUP_COLOR = { origin: '--series-5', retrieval: '--series-1', recovery: '--series-3', validation: '--series-7', pool: '--series-6', relay: '--series-2', chain: '--series-4', failure: '--status-critical' };
  const stageColor = id => { const s = stages.STAGES[stages.STAGE_INDEX[id]]; return s ? GROUP_COLOR[s.group] : '--text-muted'; };

  // ---- Ingestion ---------------------------------------------------------------------------------------------------

  // Incoming events wait here and are ingested within a per-frame time budget, so drawing never stalls. Past the cap the
  // oldest waiting batches are dropped and counted; pollers and generators slow down well before that (see busy()).
  const INGEST_BUDGET_MS = 8, MAX_PENDING = 100000;
  const incoming = [];
  let pendingEvents = 0;

  function sink(nodeKey, raw) {
    incoming.push([nodeKey, raw]);
    pendingEvents += raw.length;
    while (pendingEvents > MAX_PENDING && incoming.length > 1) {
      const [, dropped] = incoming.shift();
      pendingEvents -= dropped.length;
      state.dropped = (state.dropped || 0) + dropped.length;
    }
  }

  function drain(deadline) {
    while (incoming.length && performance.now() < deadline) {
      const [nodeKey, raw] = incoming[0];
      const chunk = raw.length > 500 ? raw.splice(0, 500) : (incoming.shift(), raw);
      pendingEvents -= chunk.length;
      ingest(nodeKey, chunk);
    }
  }

  function ingest(nodeKey, raw) {
    const added = store.ingest(nodeKey, raw);
    state.ingested += added.length;
    if (!state.paused) view.pulse(added);
    if (state.logKey) for (const e of added) if (model.eventMatches(store, e, state.filter)) state.log.push(e.gid);
    if (!$('f-node').querySelector(`option[value="${CSS.escape(nodeKey)}"]`)) refreshNodeOptions();
  }

  const busy = () => pendingEvents > 20000 || state.aggCost > 250 || (state.fps && state.fps < 20);

  /** Starts polling nodes; `proxied` ones are reached through serve.py, the rest directly. */
  function connect(nodes, proxied = false) {
    for (const { name, url } of nodes) {
      if (state.pollers.has(name)) continue;
      const node = store.node(name, { name, url });
      node.proxied = proxied;
      const poller = new sources.LivePoller(store, node, sink, { proxy: proxied, busy, onStatus: renderNodeTable });
      state.pollers.set(name, poller);
      poller.run();
    }
    refreshNodeOptions(); renderNodeTable(); saveNodes();
  }

  function disconnect(name) {
    state.pollers.get(name)?.stop();
    state.pollers.delete(name);
    renderNodeTable(); saveNodes();
  }

  function saveNodes() {
    try { localStorage.setItem('pwv.nodes', JSON.stringify([...state.pollers.values()].filter(p => !p.node.proxied).map(p => ({ name: p.node.key, url: p.node.url })))); } catch { /* storage unavailable */ }
  }

  async function discover() {
    try {
      const response = await fetch('api/nodes');
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      state.proxy = true;
      $('proxy-hint').textContent = 'Served by serve.py: node RPC calls go through its proxy.';
      connect(await response.json(), true);
    } catch {
      $('proxy-hint').textContent = 'Not served by serve.py: the page calls node RPC endpoints directly.';
    }
    if (!state.restored) {
      state.restored = true;
      try { const saved = JSON.parse(localStorage.getItem('pwv.nodes') || '[]'); if (saved.length) connect(saved); } catch { /* storage unavailable */ }
    }
  }

  function stopSchedulers() { for (const s of state.schedulers) s.stop(); state.schedulers = []; $('synthetic').value = '0'; }

  async function openCapture(capture) {
    sources.validateCapture(capture);
    stopSchedulers();
    const speed = Number($('replay-speed').value);
    if (speed) state.schedulers.push(sources.replay(capture, store, sink, speed));
    else { sources.load(capture, store, sink); fitWindow(capture); }
    refreshNodeOptions();
  }

  /** After loading a capture at once, fit the time window to its events (they are still waiting to be ingested). */
  function fitWindow(capture) {
    let last = 0, first = Infinity;
    for (const list of Object.values(capture.events)) for (const e of list) { const t = Number(e.time); if (t > last) last = t; if (t < first) first = t; }
    state.anchor = last || undefined;
    const span = last - first;
    const option = [...$('f-window').options].find(o => Number(o.value) >= span) || $('f-window').options[$('f-window').options.length - 1];
    $('f-window').value = option.value; state.windowMs = Number(option.value);
  }

  // ---- Filters -----------------------------------------------------------------------------------------------------

  function setFilter(key, value) {
    state.filter[key] = value;
    const el = $(`f-${key}`);
    if (el && el.value !== value) el.value = value;
    state.aggAt = 0; state.logKey = '';
  }

  function refreshNodeOptions() {
    const select = $('f-node');
    const current = select.value;
    select.innerHTML = '<option value="">all</option>' + [...store.nodes.values()].map(n => `<option value="${esc(n.key)}">${esc(n.name)}</option>`).join('');
    select.value = current;
    $('network-legend').innerHTML = Object.values(network.KINDS).map(k => `<span><i style="background:var(${k.color})"></i>${esc(k.label)}</span>`).join('');
  }

  // ---- Rendering ---------------------------------------------------------------------------------------------------

  function frame() {
    state.frames++;
    const now = performance.now();
    drain(now + INGEST_BUDGET_MS);
    if (now - state.fpsAt >= 1000) {
      state.fps = Math.round(state.frames * 1000 / (now - state.fpsAt)); state.frames = 0; state.fpsAt = now;
      state.rate = Math.round(state.ingested * 1000 / (now - state.rateAt)); state.ingested = 0; state.rateAt = now;
      renderPerf();
    }
    if (!state.paused) {
      if (view.animate) view.draw();
      const interval = Math.max(1000, state.aggCost * 4);
      const key = JSON.stringify([state.filter.node, state.filter.mode, state.filter.outcome, state.windowMs, state.tab]);
      if ((store.version !== state.aggVersion || key !== state.aggKey) && now - state.aggAt > interval || !state.aggAt) {
        state.aggAt = now; state.aggVersion = store.version; state.aggKey = key;
        const started = performance.now();
        renderAggregates();
        state.aggCost = performance.now() - started;
        if (!view.animate) view.draw();
      }
      if (state.tab === 'log' && (store.version !== state.logVersion || !state.logKey) && now - state.logAt > 200) { state.logAt = now; renderLog(); }
    }
    requestAnimationFrame(frame);
  }

  function renderPerf() {
    const mem = performance.memory ? ` · heap ${fmtBytes(performance.memory.usedJSHeapSize)}` : '';
    const pending = state.schedulers.reduce((a, s) => a + (s.scheduler || s).pending, 0);
    const behind = pendingEvents ? ` · ${fmtCount(pendingEvents)} to ingest` : '';
    const dropped = state.dropped ? ` · ${fmtCount(state.dropped)} dropped` : '';
    const throttled = state.synthetic?.throttled ? ` · generator throttled ${state.synthetic.throttled}×` : '';
    $('perf').textContent = `${state.fps} fps · ${fmtCount(state.rate)} events/s · ${fmtCount(store.size)}/${fmtCount(store.capacity)} events · ${fmtCount(store.wrappers.size)} wrappers${pending ? ` · ${fmtCount(pending)} scheduled` : ''}${behind}${dropped}${throttled} · update ${Math.round(state.aggCost)} ms${mem}`;
  }

  function nowAnchor() { return state.anchor || Date.now(); }

  function renderAggregates() {
    const binMs = Math.max(1000, Math.round(state.windowMs / 120 / 1000) * 1000);
    const agg = model.aggregate(store, state.filter, { now: nowAnchor(), windowMs: state.windowMs, binMs, top: 10 });
    state.agg = agg;
    renderTiles(agg);
    if (state.tab === 'overview') renderOverview(agg);
    else if (state.tab === 'proving') renderProving(agg);
    else if (state.tab === 'wrapper' && state.wrapper) renderWrapper(state.wrapper, false);
  }

  function renderTiles(agg) {
    const missed = [...store.nodes.values()].reduce((a, n) => a + n.missed, 0);
    const tile = (label, value, sub = '', alert = false) => `<div class="tile${alert ? ' alert' : ''}"><div class="label">${label}</div><div class="value">${value}${sub ? ` <small>${sub}</small>` : ''}</div></div>`;
    $('tiles').innerHTML = [
      tile('Wrappers', fmtCount(agg.wrappers), `${agg.outcomes.validated + agg.outcomes.included} valid`),
      tile('Included', fmtCount(agg.outcomes.included)),
      tile('Per-hop p50', fmtMs(agg.hop.p50), `p95 ${fmtMs(agg.hop.p95)}`),
      tile('End-to-end p50', fmtMs(agg.e2e.p50), `p95 ${fmtMs(agg.e2e.p95)}`),
      tile('Envelope requests', fmtCount(agg.counts.envelopeRequests), `${fmtCount(agg.counts.envelopesRecovered)} recovered`),
      tile('Omissions', fmtCount(agg.counts.omissions), 'next announcer'),
      tile('Rejected wrappers', fmtCount(agg.outcomes.rejected), `${agg.counts.rejections} peer verdicts, ${agg.counts.violations} violations`, agg.counts.violations > 0),
      tile('Dropped / evicted', fmtCount(agg.outcomes.dropped)),
      tile('Missed events', fmtCount(missed), 'ring overwrote', missed > 0),
      tile('Buffer', `${Math.round(store.size / store.capacity * 100)}%`, `${fmtCount(store.evicted)} evicted`),
    ].join('');
  }

  function renderOverview(agg) {
    charts.heatBands($('heat'), agg, stages.STAGES, { selectedStage: state.filter.stage, onStage: id => setFilter('stage', state.filter.stage === id ? '' : id) });
    charts.histogram($('h-hop'), agg.hop, { title: 'hops', color: '--series-1' });
    charts.histogram($('h-fetch'), agg.fetch, { title: 'retrievals', color: '--series-1' });
    charts.histogram($('h-recovery'), agg.recovery, { title: 'envelope recoveries', color: '--series-3' });
    charts.histogram($('h-e2e'), agg.e2e, { title: 'propagations', color: '--series-2' });
    const metric = $('pct-metric').value;
    const binMs = Math.max(5000, Math.round(state.windowMs / 60));
    const bins = Math.ceil(state.windowMs / binMs), start = nowAnchor() - bins * binMs;
    const pct = model.percentileSeries(agg.series[metric], start, bins, binMs);
    charts.lines($('pct'), [
      { label: 'p50', color: '--series-1', points: pct.map((p, i) => p && [start + (i + 0.5) * binMs, p.p50]) },
      { label: 'p95', color: '--series-2', points: pct.map((p, i) => p && [start + (i + 0.5) * binMs, p.p95]) },
    ], { t0: start, t1: start + bins * binMs, log: true });
    charts.counts($('outcomes'), [
      { label: 'pending', value: agg.outcomes.pending, color: '--series-1', click: () => setFilter('outcome', 'pending') },
      { label: 'validated', value: agg.outcomes.validated, color: '--status-good', click: () => setFilter('outcome', 'validated') },
      { label: 'included', value: agg.outcomes.included, color: '--status-good', click: () => setFilter('outcome', 'included') },
      { label: 'rejected', value: agg.outcomes.rejected, color: '--status-critical', click: () => setFilter('outcome', 'rejected') },
      { label: 'dropped / evicted', value: agg.outcomes.dropped, color: '--status-serious', click: () => setFilter('outcome', 'dropped') },
      { label: 'envelope omissions', value: agg.counts.omissions, color: '--series-3', tip: 'PooledTransactions omitted an envelope; asked another announcer' },
      { label: 'protocol violations', value: agg.counts.violations, color: '--status-critical', tip: 'unmatched data or invalid proof: the peer was disconnected' },
      { label: 'peer disconnects', value: agg.counts.disconnects, color: '--series-8' },
    ]);
    $('slowest').innerHTML = '<tr><th>Wrapper</th><th>Mode</th><th class=num>Nodes</th><th class=num>End to end</th></tr>' + agg.slowest.map(x => {
      const w = store.wrappers.get(x.w);
      return `<tr data-wrapper="${esc(x.w)}"><td class=hash>${esc(short(x.w))}</td><td>${w?.mode ?? '?'}</td><td class=num>${w?.perNode.size ?? ''}</td><td class=num>${fmtMs(x.ms)}</td></tr>`;
    }).join('');
    const recent = [];
    for (const w of store.wrappers.values()) if (model.wrapperMatches(w, state.filter)) recent.push(w);
    recent.sort((a, b) => b.first - a.first).splice(25);
    $('recent').innerHTML = '<tr><th>First seen</th><th>Wrapper</th><th>Mode</th><th class=num>Txs</th><th class=num>Size</th><th class=num>Nodes</th><th>Outcome</th></tr>' + recent.map(w =>
      `<tr data-wrapper="${esc(w.hash)}"><td>${time(w.first)}</td><td class=hash>${esc(short(w.hash))}</td><td>${w.mode ?? '?'}</td><td class=num>${w.txs.length || ''}</td><td class=num>${w.length ? fmtBytes(w.length) : ''}</td><td class=num>${w.perNode.size}</td><td>${pill(w.outcome)}${w.included ? ` block ${w.included.block}` : ''}</td></tr>`).join('');
  }

  function renderProving(agg) {
    const S = model.summary;
    charts.histogram($('p-leaf'), S(agg.proving.leaf), { title: 'leaf proofs', color: '--series-7' });
    charts.histogram($('p-merge'), S([...agg.proving.merge, ...agg.proving.fold, ...agg.proving.discard]), { title: 'merge steps', color: '--series-7' });
    charts.histogram($('p-aggregate'), S([...(agg.aggregation.aggregated || []), ...(agg.aggregation.reaggregated || [])]), { title: 'aggregations', color: '--series-5' });
    charts.histogram($('p-verify'), S(agg.verify.cold), { title: 'cold validations', color: '--series-1' });
    charts.histogram($('p-claims'), S(agg.verify.claims), { title: 'claimed-proof checks', color: '--series-1' });
    charts.counts($('p-cache'), [
      { label: 'wrapper verdict cache hits', value: agg.verify.cached, color: '--status-good', tip: 'EIP-8288 verdict cache: no proof verification needed' },
      { label: 'cold wrapper validations', value: agg.verify.cold.length, color: '--series-1' },
      { label: 'recursive verify: cached', value: agg.verify.recursiveCached, color: '--status-good' },
      { label: 'recursive verify: cold', value: agg.verify.recursiveCold.length, color: '--series-1', tip: `p50 ${fmtMs(S(agg.verify.recursiveCold).p50)}` },
      { label: 'native proof cache hits', value: agg.proving.cacheHits, color: '--status-good', tip: 'production proof cache: an identical step was already proven' },
      { label: 'native proofs', value: agg.proving.leaf.length + agg.proving.merge.length + agg.proving.fold.length + agg.proving.discard.length, color: '--series-7' },
      { label: 'reaggregations', value: (agg.aggregation.reaggregated || []).length, color: '--series-5', tip: 'extended the previous aggregate instead of proving from scratch' },
      { label: 'reused cached aggregate', value: (agg.aggregation['reused cached proof'] || []).length, color: '--status-good' },
    ]);
    charts.production($('p-production'), agg.production);
    charts.histogram($('p-production-proof'), S(agg.productionProofs.filter(p => p.result === 'proven').map(p => p.ms)), { title: 'scheduled production proofs', color: '--series-2' });
    charts.histogram($('p-sizes'), S(agg.sizes[1]), { title: 'mode-1 wrappers', color: '--series-2', edges: charts.BYTE_EDGES, format: fmtBytes });
    const metric = $('gauge-metric').value;
    const t1 = nowAnchor(), t0 = t1 - state.windowMs;
    const nodes = [...store.nodes.values()].filter(n => !state.filter.node || n.key === state.filter.node);
    charts.lines($('gauges'), nodes.map(n => ({ label: n.name, color: `--node-${n.index % 8 + 1}`, points: (n.gauges.get(metric) || []).filter(p => p[0] >= t0) })),
      { t0, t1, format: /Bytes/.test(metric) ? fmtBytes : v => fmtCount(Math.round(v)) });
    $('gauge-legend').innerHTML = nodes.map(n => `<span><i style="background:var(--node-${n.index % 8 + 1})"></i>${esc(n.name)}</span>`).join('');
  }

  // ---- Wrapper drilldown ---------------------------------------------------------------------------------------------

  function findWrapper(query) {
    const q = query.trim().toLowerCase();
    if (!q) return null;
    if (store.wrappers.has(q)) return q;
    const owners = store.txToWrappers.get(q);
    if (owners?.size) return owners.values().next().value;
    for (const hash of store.wrappers.keys()) if (hash.startsWith(q) || hash.slice(2).startsWith(q)) return hash;
    for (const [tx, set] of store.txToWrappers) if (tx.startsWith(q) || tx.slice(2).startsWith(q)) return set.values().next().value;
    return null;
  }

  function openWrapper(hash) {
    state.wrapper = hash;
    $('w-search').value = hash;
    selectTab('wrapper');
    renderWrapper(hash, true);
  }

  function renderWrapper(hash, full) {
    const w = store.wrappers.get(hash);
    if (!w) { $('w-header').innerHTML = `<p class=hint>No wrapper matches. It may have been evicted from the client buffer.</p>`; $('w-swimlane').innerHTML = ''; $('w-steps').innerHTML = ''; return; }
    const events = w.events.map(g => store.get(g)).filter(Boolean).sort((a, b) => a.t - b.t);
    $('w-header').innerHTML = `
      <h2><span class=hash>${esc(w.hash)}</span> ${pill(w.outcome)}${w.included ? ` <span class=note>included in block ${w.included.block}</span>` : ''}</h2>
      <table><tr><th>Mode</th><td>${w.mode === 0 ? '0 – direct witnesses, one per dependency' : w.mode === 1 ? '1 – one recursive STARK over all dependencies' : 'unknown'}</td>
      <th>Size</th><td>${w.length ? fmtBytes(w.length) : '–'}</td><th>Nodes</th><td>${w.perNode.size}</td><th>Span</th><td>${fmtMs(w.last - w.first)}</td></tr>
      <tr><th>Transactions</th><td colspan=7 class=hash>${w.txs.map(esc).join('<br>') || '–'}</td></tr>
      <tr><th>Dependencies</th><td colspan=7 class=hash>${w.deps.map(d => { const [scheme, data, key] = d.split(':'); return `${esc(scheme)} data ${esc(short(data))} key ${esc(short(key))}`; }).join('<br>') || '–'}</td></tr></table>`;
    renderSwimlane(w, events);
    if (full || events.length !== state.wrapperSteps) {
      state.wrapperSteps = events.length;
      $('w-steps').innerHTML = '<tr><th class=num>+ms</th><th>Node</th><th>Stage</th><th>Step</th><th>Message</th><th>Peer</th><th>Result</th><th>EIP rule</th></tr>' + events.map((e, i) => {
        const d = stages.describe(e);
        const repeated = i > 0 && stages.describe(events[i - 1]).rule === d.rule;
        return `<tr data-gid="${e.gid}"><td class=num>${(e.t - w.first).toFixed(1)}</td><td>${esc(nodeName(e.node))}</td><td><span class=swatch style="background:var(${stageColor(d.stage)})"></span>${esc(d.stage)}</td><td>${esc(d.label)}${e.durationMs !== undefined ? ` <span class=note>${fmtMs(e.durationMs)}</span>` : ''}${e.requestId !== undefined ? ` <span class=note>#${e.requestId}</span>` : ''}</td><td>${esc(d.message || '')}</td><td>${esc(peerName(e))}</td><td>${esc(e.result || '')}${e.detail ? `<br><span class=note>${esc(e.detail)}</span>` : ''}</td><td class=rule title="${esc(d.rule || '')}">${repeated ? '〃' : esc(d.rule || '')}</td></tr>`;
      }).join('');
    }
  }

  function renderSwimlane(w, events) {
    const lanes = [...w.perNode.entries()].sort((a, b) => a[1].first - b[1].first).map(([k]) => k);
    const width = $('w-swimlane').clientWidth || 800, labelW = 110, laneH = 54, top = 22;
    const height = top + lanes.length * laneH + 22;
    const span = Math.max(1, w.last - w.first);
    // Log time axis: hop-level milliseconds and slot-level seconds stay readable together.
    const xOf = t => labelW + Math.log1p(Math.max(0, t - w.first)) / Math.log1p(span) * (width - labelW - 20);
    const yOf = node => top + lanes.indexOf(node) * laneH + laneH / 2;
    const parts = [`<svg viewBox="0 0 ${width} ${height}" height="${height}" role="img" aria-label="Wrapper timeline across nodes">`];
    lanes.forEach((k, i) => {
      parts.push(`<rect class=lane x="${labelW}" y="${top + i * laneH + 4}" width="${width - labelW - 10}" height="${laneH - 8}" rx="4"></rect>`);
      parts.push(`<text x="0" y="${top + i * laneH + laneH / 2 + 4}">${esc(nodeName(k))}</text>`);
    });
    for (const tick of [0, 1, 10, 100, 1000, 10000, 60000, 600000]) {
      if (tick > span) break;
      const x = xOf(w.first + tick);
      parts.push(`<line x1="${x}" x2="${x}" y1="${top}" y2="${height - 18}" stroke="var(--grid)"></line><text x="${x}" y="${height - 4}" text-anchor="middle">${tick ? fmtMs(tick) : '0'}</text>`);
    }
    // Hops: the sender's offer to this node's first announcement.
    for (const [k, n] of w.perNode) {
      const from = store.peerNode(n.fromId);
      const src = from && w.perNode.get(from);
      if (src?.offeredAt !== undefined && n.announcedAt !== undefined && lanes.includes(from))
        parts.push(`<line x1="${xOf(src.offeredAt)}" y1="${yOf(from)}" x2="${xOf(n.announcedAt)}" y2="${yOf(k)}" stroke="var(--series-1)" stroke-width="1.5" stroke-dasharray="4 3" opacity="0.7"></line>`);
    }
    const labelled = new Set();
    const lastLabel = new Map();
    events.forEach(e => {
      if (!lanes.includes(e.node)) return;
      const d = stages.describe(e);
      const x = xOf(e.t), y = yOf(e.node) + (stages.STAGE_INDEX[d.stage] ?? 6) % 3 * 8 - 8;
      const color = stageColor(d.stage);
      const tip = `${d.label} · ${d.message || ''} · +${(e.t - w.first).toFixed(1)} ms${e.result ? ' · ' + e.result : ''}${e.detail ? ' · ' + e.detail : ''}\n${d.rule || ''}`;
      parts.push(`<circle class=mark cx="${x}" cy="${y}" r="${d.stage === 'rejected' || d.stage === 'dropped' ? 6 : 4.5}" fill="var(${color})" stroke="var(--surface-1)" stroke-width="2"><title>${esc(tip)}</title></circle>`);
      const key = `${e.node}|${d.stage}`;
      // One label per stage and lane, skipped where it would overlap the previous one.
      if (!labelled.has(key) && stages.STAGE_INDEX[d.stage] !== undefined && x - (lastLabel.get(e.node) ?? -Infinity) > 90) {
        labelled.add(key);
        lastLabel.set(e.node, x);
        parts.push(`<text x="${x + 6}" y="${y - 7}">${esc(stages.STAGES[stages.STAGE_INDEX[d.stage]].label)}</text>`);
      }
    });
    parts.push('</svg>');
    $('w-swimlane').innerHTML = parts.join('');
  }

  // ---- Event log (virtualised) ---------------------------------------------------------------------------------------

  const ROW = 22;
  function renderLog() {
    const key = JSON.stringify(state.filter);
    if (state.logKey !== key) {
      state.logKey = key;
      state.log = [];
      for (const e of store.events()) if (model.eventMatches(store, e, state.filter)) state.log.push(e.gid);
    }
    // Drop entries the ring buffer evicted.
    const min = store.minGid;
    let cut = 0;
    while (cut < state.log.length && state.log[cut] < min) cut++;
    if (cut) state.log.splice(0, cut);
    state.logVersion = store.version;
    const box = $('log'), spacer = $('log-spacer');
    spacer.style.height = `${state.log.length * ROW}px`;
    if ($('log-follow').checked) box.scrollTop = box.scrollHeight;
    const first = Math.max(0, Math.floor(box.scrollTop / ROW) - 5);
    const last = Math.min(state.log.length, first + Math.ceil(box.clientHeight / ROW) + 10);
    const rows = [];
    for (let i = first; i < last; i++) {
      const e = store.get(state.log[i]);
      if (!e) continue;
      const d = stages.describe(e);
      rows.push(`<div class="log-row" style="top:${i * ROW}px" data-gid="${e.gid}"><span>${time(e.t)}</span><span>${esc(nodeName(e.node))}</span><span><span class=swatch style="background:var(${stageColor(d.stage)})"></span>${esc(d.stage)}</span><span title="${esc(d.rule || '')}">${esc(d.label)}${d.message ? ` <span class=note>${esc(d.message)}</span>` : ''}</span><span class=hash>${esc(short(e.wrapper || (e.transactions || [])[0] || ''))}</span><span>${esc(peerName(e))}</span><span>${esc(e.result || '')} <span class=note>${esc(e.detail || '')}</span></span></div>`);
    }
    box.querySelectorAll('.log-row').forEach(r => r.remove());
    box.insertAdjacentHTML('beforeend', rows.join(''));
    $('log-count').textContent = `${fmtCount(state.log.length)} matching events`;
  }

  // ---- Node table, tabs and guide -----------------------------------------------------------------------------------

  function renderNodeTable() {
    $('node-table').querySelector('tbody').innerHTML = [...store.nodes.values()].map(n => `<tr><td><span class=swatch style="background:var(--node-${n.index % 8 + 1})"></span>${esc(n.name)}</td><td class=hash>${esc(n.url || 'capture')}</td><td>${esc(n.status)}${n.error ? ` <span class=note>${esc(n.error)}</span>` : ''}</td><td class=num>${n.after}</td><td class=num>${n.missed}</td><td>${state.pollers.has(n.key) ? `<button data-disconnect="${esc(n.key)}">Stop</button>` : ''}</td></tr>`).join('');
  }

  function selectTab(tab) {
    state.tab = tab;
    document.querySelectorAll('.tabs button').forEach(b => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
    document.querySelectorAll('.tab').forEach(t => { t.hidden = t.id !== `tab-${tab}`; });
    state.aggAt = 0; state.logKey = tab === 'log' ? state.logKey : '';
  }

  function renderGuide() {
    $('guide').innerHTML = `<h2>Reading the dashboard</h2>
      <p>Each node records what happens to proof wrappers in a bounded ring buffer, read with <code>debug_proofWrapperEvents</code>. The page polls every node by cursor, keeps the newest ${fmtCount(store.capacity)} events and ${fmtCount(store.maxWrappers)} wrappers, and draws aggregates first; open a wrapper from a table, the log or the search box to follow it step by step across nodes.</p>
      <h2>Lifecycle stages</h2><dl>${stages.STAGES.map(s => `<dt><span class=swatch style="background:var(${GROUP_COLOR[s.group]})"></span>${esc(s.label)}</dt><dd>${esc(s.blurb)}</dd>`).join('')}</dl>
      <h2>Spec rules shown on each step</h2><dl>${Object.values(stages.RULES).map(r => `<dd>${esc(r)}</dd>`).join('')}</dl>`;
  }

  // ---- Wiring ------------------------------------------------------------------------------------------------------

  document.querySelectorAll('.tabs button').forEach(b => b.addEventListener('click', () => selectTab(b.dataset.tab)));
  $('toggle-sources').addEventListener('click', () => { const p = $('sources'); p.hidden = !p.hidden; $('toggle-sources').setAttribute('aria-expanded', String(!p.hidden)); renderNodeTable(); });
  $('add-nodes').addEventListener('click', () => { connect(sources.parseNodes($('node-input').value, store.nodes.size)); $('node-input').value = ''; });
  $('discover').addEventListener('click', discover);
  $('node-table').addEventListener('click', ev => { const k = ev.target.dataset?.disconnect; if (k) disconnect(k); });
  $('import').addEventListener('change', async ev => {
    const file = ev.target.files[0];
    if (!file) return;
    try { await openCapture(JSON.parse(await file.text())); } catch (error) { alert(`Could not open capture: ${error.message}`); }
    ev.target.value = '';
  });
  $('demo').addEventListener('click', async () => {
    try { const r = await fetch('demo/devnet-capture.json'); if (!r.ok) throw new Error(`HTTP ${r.status}`); await openCapture(await r.json()); }
    catch (error) { alert(`Could not fetch the demo capture (${error.message}). Opened from disk, use "Open capture" on demo/devnet-capture.json instead.`); }
  });
  $('synthetic').addEventListener('change', ev => {
    for (const s of state.schedulers) s.stop();
    state.schedulers = [];
    const rate = Number(ev.target.value);
    if (!rate) return;
    const synthetic = new sources.Synthetic(store, sink, { rate, busy });
    state.synthetic = synthetic;
    synthetic.start();
    state.schedulers.push({ stop: () => synthetic.stop(), get pending() { return synthetic.scheduler.pending; } });
    state.anchor = undefined;
    refreshNodeOptions();
  });
  $('export').addEventListener('click', () => {
    const blob = new Blob([JSON.stringify(store.toJSON())], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `eip8437-capture-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  });
  $('pause').addEventListener('click', () => { state.paused = !state.paused; $('pause').setAttribute('aria-pressed', String(state.paused)); $('pause').textContent = state.paused ? 'Resume' : 'Pause'; });
  $('clear').addEventListener('click', () => {
    const keep = [...store.nodes.values()].filter(n => state.pollers.has(n.key)).map(n => ({ key: n.key, name: n.name, url: n.url, enode: n.enode, after: n.after }));
    store.clear();
    for (const n of keep) { store.node(n.key, n); state.pollers.get(n.key).node = store.nodes.get(n.key); }
    state.logKey = ''; state.aggAt = 0; state.wrapper = null; state.anchor = undefined;
    refreshNodeOptions();
  });
  $('animate').addEventListener('change', ev => { view.animate = ev.target.checked; });
  for (const key of ['node', 'mode', 'stage', 'outcome']) $(`f-${key}`).addEventListener('change', ev => setFilter(key, ev.target.value));
  let textTimer;
  $('f-text').addEventListener('input', ev => {
    clearTimeout(textTimer);
    textTimer = setTimeout(() => {
      const hash = findWrapper(ev.target.value);
      if (hash && ev.target.value.length >= 10) openWrapper(hash);
      setFilter('text', ev.target.value);
    }, 250);
  });
  $('f-window').addEventListener('change', ev => { state.windowMs = Number(ev.target.value); state.aggAt = 0; });
  $('pct-metric').addEventListener('change', () => { state.aggAt = 0; });
  $('gauge-metric').addEventListener('change', () => { state.aggAt = 0; });
  $('w-go').addEventListener('click', () => { const h = findWrapper($('w-search').value); h ? openWrapper(h) : renderWrapper('', true); });
  $('w-search').addEventListener('keydown', ev => { if (ev.key === 'Enter') $('w-go').click(); });
  document.addEventListener('click', ev => {
    const row = ev.target.closest('[data-wrapper]');
    if (row) return openWrapper(row.dataset.wrapper);
    const logged = ev.target.closest('.log-row[data-gid]');
    const e = logged && store.get(Number(logged.dataset.gid));
    if (e?.wrapper) openWrapper(e.wrapper);
    else if (e?.transactions?.length) { const h = findWrapper(e.transactions[0]); if (h) openWrapper(h); }
  });
  $('log').addEventListener('scroll', () => { if (!$('log-follow').checked || state.logAt) { state.logVersion = -1; } });
  $('log').addEventListener('wheel', () => { $('log-follow').checked = false; });

  for (const s of stages.STAGES) $('f-stage').insertAdjacentHTML('beforeend', `<option value="${s.id}">${esc(s.label)}</option>`);
  for (const extra of ['proving', 'network', 'metrics']) $('f-stage').insertAdjacentHTML('beforeend', `<option value="${extra}">${extra}</option>`);
  refreshNodeOptions();
  renderGuide();
  discover();
  const params = new URLSearchParams(location.search);
  if (params.get('synthetic')) { $('synthetic').value = params.get('synthetic'); $('synthetic').dispatchEvent(new Event('change')); }
  if (params.get('demo')) $('demo').click();
  if (params.get('tab')) selectTab(params.get('tab'));
  window.PWV.app = { store, state, openWrapper, findWrapper, selectTab, setFilter };
  requestAnimationFrame(frame);
})();
