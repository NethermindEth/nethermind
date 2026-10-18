// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Nodes and their eth/73 peers, with EIP-8437 messages animated along the edges. Animations are budgeted: past the budget,
// messages on an edge are batched into a count badge instead of more moving dots, so high event rates never stall drawing.
(function (root) {
  'use strict';
  const { css, esc } = root.PWV.charts;

  const KINDS = {
    announce: { label: 'NewProofWrapperHashes', color: '--series-1' },
    request: { label: 'GetProofWrappers', color: '--series-7' },
    response: { label: 'ProofWrappers', color: '--series-2' },
    envelopes: { label: 'Get/PooledTransactions', color: '--series-3' },
  };
  // Sender-side events: [kind, direction] where 'out' runs from the recording node to its peer.
  const SENT = { announced: ['announce', 'out'], 'wrapper-requested': ['request', 'out'], served: ['response', 'out'], 'envelope-requested': ['envelopes', 'out'] };
  // Receiver-side events, used only when the sender is not monitored, so no message is animated twice.
  const RECEIVED = { 'announcement-received': 'announce', 'wrapper-received': 'response', 'envelope-response': 'envelopes' };
  const MAX_PARTICLES = 80;
  const DURATION_MS = 700;
  const MAX_EXTERNAL = 16;

  class NetworkView {
    constructor(canvas, store, { onNode } = {}) {
      this.canvas = canvas; this.store = store; this.onNode = onNode;
      this.particles = []; this.bursts = new Map(); this.positions = new Map(); this.animate = true;
      this.dropped = 0;
      canvas.addEventListener('click', ev => {
        const b = canvas.getBoundingClientRect(), x = ev.clientX - b.left, y = ev.clientY - b.top;
        for (const [key, p] of this.positions) if (p.monitored && Math.hypot(p.x - x, p.y - y) < 22) return this.onNode?.(key);
      });
      canvas.addEventListener('mousemove', ev => {
        const b = canvas.getBoundingClientRect(), x = ev.clientX - b.left, y = ev.clientY - b.top;
        for (const [key, p] of this.positions) {
          if (Math.hypot(p.x - x, p.y - y) < 22) {
            const node = this.store.nodes.get(key);
            const g = k => node?.gauges.get(k)?.at(-1)?.[1] ?? '–';
            root.PWV.charts.showTip(ev.clientX, ev.clientY, node
              ? `<b>${esc(node.name)}</b> ${esc(node.client || '')}<br>status ${esc(node.status)}${node.error ? ' – ' + esc(node.error) : ''}<br>peers ${g('peers')}, pending announcements ${g('pending')}, validation queue ${g('validation')}<br>offered wrappers ${g('stored')}, pool ${g('pool')}<br>events through seq ${node.after}${node.missed ? `, <b>${node.missed} missed</b> (ring overwrote)` : ''}<br>click to filter by this node`
              : `external peer ${esc(key.slice(0, 16))}…`);
            canvas.style.cursor = node ? 'pointer' : 'default';
            return;
          }
        }
        root.PWV.charts.hideTip(); canvas.style.cursor = 'default';
      });
      canvas.addEventListener('mouseleave', () => root.PWV.charts.hideTip());
    }

    /** Queues animations for newly ingested events, batching what exceeds the budget. */
    pulse(events) {
      if (!this.animate) return;
      const now = performance.now();
      for (const e of events) {
        let kind, from, to;
        const peer = e.peerNode || this.store.peerNode(e.peerId) || (e.peerId ? 'ext:' + e.peerId.toLowerCase().replace(/^0x/, '') : undefined);
        if (!peer) continue;
        if (SENT[e.type]) { [kind] = SENT[e.type]; from = e.node; to = peer; }
        else if (RECEIVED[e.type] && peer.startsWith('ext:')) { kind = RECEIVED[e.type]; from = peer; to = e.node; }
        else continue;
        if (this.particles.length < MAX_PARTICLES) this.particles.push({ from, to, kind, start: now + Math.random() * 120 });
        else {
          const key = [from, to].sort().join('|');
          const b = this.bursts.get(key) || { from, to, count: 0, until: 0 };
          b.count++; b.until = now + 1000;
          this.bursts.set(key, b);
          this.dropped++;
        }
      }
    }

    layout(width, height) {
      const monitored = [...this.store.nodes.keys()];
      const cx = width / 2, cy = height / 2, r = Math.min(width, height) / 2 - 46;
      this.positions.clear();
      monitored.forEach((key, i) => {
        const a = -Math.PI / 2 + 2 * Math.PI * i / Math.max(1, monitored.length);
        this.positions.set(key, { x: cx + r * Math.cos(a), y: cy + r * Math.sin(a), monitored: true, index: i });
      });
      const external = new Map();
      for (const node of this.store.nodes.values())
        for (const id of [...node.links, ...(node.adminPeers || [])])
          if (!this.store.peerIds.has(id) && external.size < MAX_EXTERNAL) external.set('ext:' + id, node.key);
      let i = 0;
      for (const [key, owner] of external) {
        const p = this.positions.get(owner);
        const a = Math.atan2(p.y - cy, p.x - cx) + (i++ % 3 - 1) * 0.35;
        this.positions.set(key, { x: p.x + 36 * Math.cos(a), y: p.y + 36 * Math.sin(a), monitored: false });
      }
      this.external = external;
    }

    /** Every link seen, and whether either side currently reports it connected. */
    edges() {
      const list = new Map();
      for (const node of this.store.nodes.values()) {
        const connected = new Set([...node.peers, ...(node.adminPeers || [])]);
        for (const id of new Set([...node.links, ...connected])) {
          const other = this.store.peerIds.get(id) || 'ext:' + id;
          if (!this.positions.has(other)) continue;
          const key = [node.key, other].sort().join('|');
          list.set(key, list.get(key) || connected.has(id));
        }
      }
      return [...list].map(([key, live]) => [...key.split('|'), live]);
    }

    draw() {
      const canvas = this.canvas, dpr = window.devicePixelRatio || 1;
      const width = canvas.clientWidth, height = canvas.clientHeight;
      if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) { canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr); }
      const ctx = canvas.getContext('2d');
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, width, height);
      ctx.font = '11px system-ui, -apple-system, "Segoe UI", sans-serif';
      this.layout(width, height);
      const now = performance.now();
      ctx.strokeStyle = css('--axis'); ctx.lineWidth = 1.5;
      for (const [a, b, live] of this.edges()) {
        const p = this.positions.get(a), q = this.positions.get(b);
        ctx.setLineDash(live ? [] : [4, 4]);
        ctx.beginPath(); ctx.moveTo(p.x, p.y); ctx.lineTo(q.x, q.y); ctx.stroke();
      }
      ctx.setLineDash([]);
      // Batched traffic: a thicker edge with the message count.
      for (const [key, b] of this.bursts) {
        if (b.until < now) { this.bursts.delete(key); continue; }
        const p = this.positions.get(b.from), q = this.positions.get(b.to);
        if (!p || !q) continue;
        ctx.strokeStyle = css('--series-1'); ctx.lineWidth = 4; ctx.globalAlpha = 0.5;
        ctx.beginPath(); ctx.moveTo(p.x, p.y); ctx.lineTo(q.x, q.y); ctx.stroke();
        ctx.globalAlpha = 1; ctx.lineWidth = 1.5;
        ctx.fillStyle = css('--text-primary');
        ctx.fillText(`+${b.count}`, (p.x + q.x) / 2 + 4, (p.y + q.y) / 2 - 4);
      }
      this.particles = this.particles.filter(m => now - m.start < DURATION_MS);
      for (const m of this.particles) {
        const p = this.positions.get(m.from), q = this.positions.get(m.to);
        if (!p || !q || now < m.start) continue;
        const f = (now - m.start) / DURATION_MS;
        ctx.fillStyle = css(KINDS[m.kind].color);
        ctx.beginPath(); ctx.arc(p.x + (q.x - p.x) * f, p.y + (q.y - p.y) * f, 4.5, 0, 2 * Math.PI); ctx.fill();
      }
      for (const [key, p] of this.positions) {
        const node = this.store.nodes.get(key);
        ctx.beginPath(); ctx.arc(p.x, p.y, p.monitored ? 18 : 6, 0, 2 * Math.PI);
        ctx.fillStyle = css('--surface-1'); ctx.fill();
        ctx.lineWidth = 2; ctx.strokeStyle = p.monitored ? css(`--node-${p.index % 8 + 1}`) : css('--axis'); ctx.stroke(); ctx.lineWidth = 1.5;
        if (!p.monitored) continue;
        if (node.status === 'error') { ctx.fillStyle = css('--status-critical'); ctx.fillText('!', p.x - 2, p.y + 4); }
        ctx.fillStyle = css('--text-primary'); ctx.textAlign = 'center';
        ctx.fillText(node.name, p.x, p.y + 32);
        const stored = node.gauges.get('stored')?.at(-1)?.[1];
        if (stored !== undefined) { ctx.fillStyle = css('--text-secondary'); ctx.fillText(String(stored), p.x, p.y + 4); }
        ctx.textAlign = 'left';
      }
    }
  }

  root.PWV = Object.assign(root.PWV || {}, { network: { NetworkView, KINDS } });
})(window);
