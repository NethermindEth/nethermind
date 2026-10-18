// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Small canvas charts: cost stays flat however many events feed them, and each keeps hit regions for hover tooltips.
(function (root) {
  'use strict';

  const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
  /** Escapes text from nodes or capture files before it goes into tooltip HTML. */
  const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ESCAPES[c]);
  const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  const fmtMs = ms => !Number.isFinite(ms) ? '–' : ms < 1 ? `${ms.toFixed(2)} ms` : ms < 1000 ? `${ms.toFixed(ms < 10 ? 1 : 0)} ms` : `${(ms / 1000).toFixed(ms < 10000 ? 2 : 1)} s`;
  const fmtBytes = b => !Number.isFinite(b) ? '–' : b < 1024 ? `${b} B` : b < 1048576 ? `${(b / 1024).toFixed(1)} KiB` : `${(b / 1048576).toFixed(2)} MiB`;
  const fmtCount = n => n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e4 ? `${(n / 1e3).toFixed(1)}k` : String(n);
  const fmtTime = t => new Date(t).toLocaleTimeString([], { hour12: false });

  let tooltip;
  function showTip(x, y, html) {
    tooltip ||= document.getElementById('tooltip');
    tooltip.innerHTML = html;
    tooltip.hidden = false;
    const r = tooltip.getBoundingClientRect();
    tooltip.style.left = `${Math.min(window.innerWidth - r.width - 8, x + 14)}px`;
    tooltip.style.top = `${Math.min(window.innerHeight - r.height - 8, y + 14)}px`;
  }
  function hideTip() { if (tooltip) tooltip.hidden = true; }

  /** Sizes a canvas to its CSS box at device resolution and wires hover over the regions a draw registers. */
  function prepare(canvas, height) {
    const dpr = window.devicePixelRatio || 1;
    const width = canvas.clientWidth || canvas.parentElement.clientWidth;
    if (height) canvas.style.height = `${height}px`;
    const h = canvas.clientHeight;
    if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(width * dpr); canvas.height = Math.round(h * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, h);
    ctx.font = '11px system-ui, -apple-system, "Segoe UI", sans-serif';
    canvas.regions = [];
    if (!canvas.hoverWired) {
      canvas.hoverWired = true;
      canvas.addEventListener('mousemove', ev => {
        const b = canvas.getBoundingClientRect(), x = ev.clientX - b.left, y = ev.clientY - b.top;
        const hit = (canvas.regions || []).find(r => x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h);
        if (hit) { showTip(ev.clientX, ev.clientY, hit.tip); canvas.style.cursor = hit.click ? 'pointer' : 'default'; }
        else { hideTip(); canvas.style.cursor = 'default'; }
      });
      canvas.addEventListener('mouseleave', hideTip);
      canvas.addEventListener('click', ev => {
        const b = canvas.getBoundingClientRect(), x = ev.clientX - b.left, y = ev.clientY - b.top;
        (canvas.regions || []).find(r => r.click && x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h)?.click();
      });
    }
    return { ctx, width, height: h };
  }

  function empty(ctx, width, height, text) {
    ctx.fillStyle = css('--text-muted');
    ctx.textAlign = 'center';
    ctx.fillText(text, width / 2, height / 2);
    ctx.textAlign = 'left';
  }

  /** Stage × time heat bands: one row per lifecycle stage, darker for more events in the bin. */
  function heatBands(canvas, agg, stages, { selectedStage, onStage } = {}) {
    const rowH = 18, gap = 2, labelW = 128, axisH = 18;
    const { ctx, width } = prepare(canvas, stages.length * (rowH + gap) + axisH);
    const plotW = width - labelW - 8;
    const cellW = plotW / agg.bins;
    let max = 1;
    for (const row of agg.heat) for (const v of row) if (v > max) max = v;
    const ramp = [css('--seq-100'), css('--seq-250'), css('--seq-400'), css('--seq-550'), css('--seq-700')];
    stages.forEach((s, i) => {
      const y = i * (rowH + gap);
      ctx.fillStyle = s.id === selectedStage ? css('--text-primary') : css('--text-secondary');
      ctx.fillText(s.label, 0, y + 13);
      canvas.regions.push({ x: 0, y, w: labelW, h: rowH, tip: `<b>${esc(s.label)}</b><br>${esc(s.blurb)}`, click: onStage && (() => onStage(s.id)) });
      ctx.fillStyle = css('--grid');
      ctx.fillRect(labelW, y, plotW, rowH);
      const row = agg.heat[i];
      for (let b = 0; b < agg.bins; b++) {
        const v = row[b];
        if (!v) continue;
        const level = Math.min(ramp.length - 1, Math.floor(Math.log(v) / Math.log(max + 1) * ramp.length));
        ctx.fillStyle = ramp[level];
        const x = labelW + b * cellW;
        ctx.fillRect(x + 0.5, y, Math.max(1, cellW - 1), rowH);
        canvas.regions.push({ x, y, w: cellW, h: rowH, tip: `<b>${esc(s.label)}</b>: ${v} event${v > 1 ? 's' : ''}<br>${fmtTime(agg.start + b * agg.binMs)} + ${agg.binMs / 1000}s` });
      }
    });
    axis(ctx, labelW, stages.length * (rowH + gap) + 12, plotW, agg.start, agg.start + agg.bins * agg.binMs);
  }

  function axis(ctx, x0, y, w, t0, t1) {
    ctx.fillStyle = css('--text-muted');
    const ticks = Math.max(2, Math.floor(w / 110));
    for (let i = 0; i <= ticks; i++) {
      const t = t0 + (t1 - t0) * i / ticks;
      const x = x0 + w * i / ticks;
      ctx.textAlign = i === 0 ? 'left' : i === ticks ? 'right' : 'center';
      ctx.fillText(fmtTime(t), x, y);
    }
    ctx.textAlign = 'left';
  }

  const EDGES = [0, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 60000, Infinity];
  const BYTE_EDGES = [0, 1024, 4096, 16384, 65536, 131072, 262144, 393216, 524288, 1048576, 2097152, Infinity];

  /** Latency histogram on log-spaced bins (milliseconds by default), with p50 / p95 / p99 called out. */
  function histogram(canvas, summary, { title, color = '--series-1', height = 120, edges = EDGES, format = fmtMs } = {}) {
    const { ctx, width } = prepare(canvas, height);
    const values = summary.sorted;
    if (!values.length) return empty(ctx, width, height, `No ${title || 'data'} yet`);
    const counts = new Array(edges.length - 1).fill(0);
    let b = 0;
    for (const v of values) { while (v >= edges[b + 1]) b++; counts[b]++; }
    let lo = counts.findIndex(c => c > 0), hi = counts.length - 1 - [...counts].reverse().findIndex(c => c > 0);
    lo = Math.max(0, lo - 1); hi = Math.min(counts.length - 1, hi + 1);
    const n = hi - lo + 1, plotH = height - 34, barW = (width - 4) / n, max = Math.max(...counts);
    for (let i = lo; i <= hi; i++) {
      const x = (i - lo) * barW + 2, h = counts[i] / max * plotH;
      ctx.fillStyle = css(color);
      if (counts[i]) roundTop(ctx, x + 1, 16 + plotH - h, Math.max(2, barW - 2), Math.max(2, h));
      const label = edges[i + 1] === Infinity ? `≥${format(edges[i])}` : `${format(edges[i])}–${format(edges[i + 1])}`;
      canvas.regions.push({ x, y: 16, w: barW, h: plotH, tip: `${label}: <b>${counts[i]}</b> (${(counts[i] / values.length * 100).toFixed(1)}%)` });
      ctx.fillStyle = css('--text-muted');
      if (barW > 34 || i % 2 === lo % 2) { ctx.textAlign = 'left'; ctx.fillText(edges[i] === 0 ? '0' : format(edges[i]).replace(' ', ''), x, height - 4); }
    }
    ctx.textAlign = 'left';
    ctx.fillStyle = css('--text-secondary');
    ctx.fillText(`n=${fmtCount(values.length)} · p50 ${format(summary.p50)} · p95 ${format(summary.p95)} · max ${format(summary.max)}`, 2, 11);
    canvas.regions.push({ x: 0, y: 0, w: width, h: 14, tip: `n=${values.length}<br>p50 ${format(summary.p50)}<br>p95 ${format(summary.p95)}<br>p99 ${format(summary.p99)}<br>max ${format(summary.max)}` });
  }

  function roundTop(ctx, x, y, w, h) {
    const r = Math.min(4, w / 2, h);
    ctx.beginPath();
    ctx.moveTo(x, y + h); ctx.lineTo(x, y + r); ctx.quadraticCurveTo(x, y, x + r, y);
    ctx.lineTo(x + w - r, y); ctx.quadraticCurveTo(x + w, y, x + w, y + r); ctx.lineTo(x + w, y + h);
    ctx.fill();
  }

  /** Line chart of several series over time, one y scale; gaps where a bin has no data. */
  function lines(canvas, series, { t0, t1, height = 130, format = fmtMs, log = false } = {}) {
    const { ctx, width } = prepare(canvas, height);
    const pad = { l: 52, r: 8, t: 8, b: 18 };
    let max = 0, min = Infinity;
    for (const s of series) for (const p of s.points) if (p && Number.isFinite(p[1])) { max = Math.max(max, p[1]); min = Math.min(min, p[1]); }
    if (min === Infinity) return empty(ctx, width, height, 'No data in this window');
    if (!max) { max = 1; log = false; }
    const plotW = width - pad.l - pad.r, plotH = height - pad.t - pad.b;
    const lo = log ? Math.max(0.1, min) : 0;
    const yOf = v => pad.t + plotH - (log ? Math.log(Math.max(v, lo) / lo) / Math.log(max / lo || 10) : v / max) * plotH;
    const xOf = t => pad.l + (t - t0) / (t1 - t0) * plotW;
    ctx.strokeStyle = css('--grid'); ctx.fillStyle = css('--text-muted');
    for (let i = 0; i <= 3; i++) {
      const v = log ? lo * Math.pow(max / lo, i / 3) : max * i / 3, y = yOf(v);
      ctx.beginPath(); ctx.moveTo(pad.l, y); ctx.lineTo(width - pad.r, y); ctx.stroke();
      ctx.textAlign = 'right'; ctx.fillText(format(v), pad.l - 4, y + 4);
    }
    ctx.textAlign = 'left';
    axis(ctx, pad.l, height - 4, plotW, t0, t1);
    ctx.lineWidth = 2; ctx.lineJoin = 'round';
    for (const s of series) {
      ctx.strokeStyle = css(s.color); ctx.beginPath();
      let pen = false;
      for (const p of s.points) {
        if (!p || !Number.isFinite(p[1])) { pen = false; continue; }
        const x = xOf(p[0]), y = yOf(p[1]);
        pen ? ctx.lineTo(x, y) : ctx.moveTo(x, y); pen = true;
      }
      ctx.stroke();
    }
    ctx.lineWidth = 1;
    // One hover column per x position, listing every series.
    const columns = Math.min(120, Math.max(...series.map(s => s.points.length)));
    for (let c = 0; c < columns; c++) {
      const t = t0 + (t1 - t0) * (c + 0.5) / columns;
      const rows = series.map(s => {
        let best; for (const p of s.points) if (p && (!best || Math.abs(p[0] - t) < Math.abs(best[0] - t))) best = p;
        return best && Math.abs(best[0] - t) < (t1 - t0) / columns ? `<span class=swatch style="background:${css(s.color)}"></span>${esc(s.label)}: <b>${format(best[1])}</b>` : '';
      }).filter(Boolean);
      if (rows.length) canvas.regions.push({ x: pad.l + plotW * c / columns, y: pad.t, w: plotW / columns, h: plotH, tip: `${fmtTime(t)}<br>${rows.join('<br>')}` });
    }
  }

  /** Per-block production proving: bar height is time to a usable proof; misses are marked and labelled. */
  function production(canvas, passes, { height = 130, onBlock } = {}) {
    const { ctx, width } = prepare(canvas, height);
    if (!passes.length) return empty(ctx, width, height, 'No block production with EIP-8288 dependencies yet');
    const recent = passes.slice(-60);
    const pad = { l: 52, b: 18, t: 8 }, plotW = width - pad.l - 4, plotH = height - pad.t - pad.b;
    const max = Math.max(1, ...recent.map(p => p.ms || 0));
    const w = Math.min(28, plotW / recent.length);
    ctx.fillStyle = css('--text-muted'); ctx.textAlign = 'right';
    ctx.fillText(fmtMs(max), pad.l - 4, pad.t + 8); ctx.fillText('0', pad.l - 4, pad.t + plotH);
    ctx.textAlign = 'left';
    recent.forEach((p, i) => {
      const x = pad.l + i * w, h = Math.max(2, (p.ms || 0) / max * plotH);
      const missed = p.result === 'not-ready';
      ctx.fillStyle = css(missed ? '--status-critical' : p.result === 'proven' ? '--series-2' : '--status-good');
      roundTop(ctx, x + 1, pad.t + plotH - h, Math.max(2, w - 2), h);
      if (missed && w > 8) { ctx.fillStyle = css('--status-critical'); ctx.fillText('✕', x + w / 2 - 3, pad.t + plotH - h - 2); }
      canvas.regions.push({
        x, y: pad.t, w, h: plotH, click: onBlock && (() => onBlock(p)),
        tip: `Block <b>${esc(p.block)}</b> on ${esc(p.node)}<br>${missed ? '✕ proof not ready by the deadline' : p.result === 'proven' ? 'proven on the production path' : '✓ proof ready (' + esc(p.result) + ')'}<br>${p.deps} dependencies, ${fmtMs(p.ms)}${p.bytes ? ', ' + fmtBytes(p.bytes) : ''}${p.detail ? '<br>' + esc(p.detail) : ''}`,
      });
    });
    ctx.fillStyle = css('--text-muted');
    ctx.fillText(`blocks ${recent[0].block}–${recent[recent.length - 1].block}`, pad.l, height - 4);
  }

  /** Horizontal count bars for a handful of labelled categories. */
  function counts(canvas, rows, { height } = {}) {
    const rowH = 18;
    const { ctx, width } = prepare(canvas, height || rows.length * (rowH + 4) + 2);
    const max = Math.max(1, ...rows.map(r => r.value));
    const labelW = 150;
    rows.forEach((r, i) => {
      const y = i * (rowH + 4);
      ctx.fillStyle = css('--text-secondary'); ctx.fillText(r.label, 0, y + 13);
      const w = (width - labelW - 60) * r.value / max;
      ctx.fillStyle = css(r.color || '--series-1');
      if (r.value) ctx.fillRect(labelW, y + 3, Math.max(2, w), rowH - 6);
      ctx.fillStyle = css('--text-primary'); ctx.fillText(fmtCount(r.value), labelW + w + 6, y + 13);
      canvas.regions.push({ x: 0, y, w: width, h: rowH, tip: `${esc(r.label)}: <b>${r.value}</b>${r.tip ? '<br>' + esc(r.tip) : ''}`, click: r.click });
    });
  }

  const api = { BYTE_EDGES, esc, heatBands, histogram, lines, production, counts, fmtMs, fmtBytes, fmtCount, fmtTime, css, showTip, hideTip };
  root.PWV = Object.assign(root.PWV || {}, { charts: api });
})(window);
