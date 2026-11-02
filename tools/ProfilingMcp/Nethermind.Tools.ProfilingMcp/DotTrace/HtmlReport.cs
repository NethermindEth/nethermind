// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;

namespace Nethermind.Tools.ProfilingMcp.DotTrace;

/// <summary>Renders a dotTrace snapshot as a self-contained HTML page with a hotspot table and a flame graph.</summary>
/// <remarks>
/// The page embeds its data and script and loads nothing, so it opens offline and can be shared as a single file.
/// The JSON is written with the default encoder, which escapes <c>&lt;</c>, so method names cannot close the script element.
/// </remarks>
internal static class HtmlReport
{
    private const int MaxTableRows = 500;

    public static string Render(string snapshot, IReadOnlyList<FunctionStat> functions, CallTreeNode tree, int nodesBeforePruning, Func<string, bool> isWait)
    {
        Dictionary<string, int> nameIds = new(StringComparer.Ordinal);
        List<string> names = [];
        int Id(string name)
        {
            if (!nameIds.TryGetValue(name, out int id))
            {
                nameIds[name] = id = names.Count;
                names.Add(name);
            }
            return id;
        }

        using MemoryStream body = new();
        using (Utf8JsonWriter json = new(body))
        {
            json.WriteStartObject();
            json.WriteString("snapshot", snapshot);
            json.WriteString("captured", File.GetLastWriteTimeUtc(snapshot).ToString("u"));
            json.WriteString("generated", DateTime.UtcNow.ToString("u"));
            json.WriteNumber("methods", functions.Count);
            json.WriteNumber("totalTime", tree.TotalTime);
            json.WriteNumber("waitTime", tree.WaitTime);
            json.WriteNumber("nodes", CallTree.Count(tree));
            json.WriteNumber("nodesBeforePruning", nodesBeforePruning);

            json.WritePropertyName("tree");
            WriteNode(json, tree, Id);

            json.WriteStartArray("functions");
            foreach (FunctionStat f in functions.OrderByDescending(static f => f.OwnTime).Take(MaxTableRows))
            {
                json.WriteStartArray();
                json.WriteNumberValue(Id(f.Name));
                json.WriteNumberValue(f.OwnTime);
                json.WriteNumberValue(f.TotalTime);
                json.WriteNumberValue(f.Samples ?? f.Calls ?? 0);
                json.WriteNumberValue(isWait(f.Name) ? 1 : 0);
                json.WriteEndArray();
            }
            json.WriteEndArray();

            json.WriteStartArray("waits");
            for (int i = 0; i < names.Count; i++)
            {
                if (isWait(names[i]))
                    json.WriteNumberValue(i);
            }
            json.WriteEndArray();

            json.WriteStartArray("names");
            foreach (string name in names)
                json.WriteStringValue(name);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Template
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(Path.GetFileName(snapshot)), StringComparison.Ordinal)
            .Replace("__DATA__", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    /// <remarks>A node is <c>[name id, total, own, wait, samples, [children]]</c>; children are sorted by total time, largest first.</remarks>
    private static void WriteNode(Utf8JsonWriter json, CallTreeNode node, Func<string, int> id)
    {
        json.WriteStartArray();
        json.WriteNumberValue(id(node.Name));
        json.WriteNumberValue(node.TotalTime);
        json.WriteNumberValue(node.OwnTime);
        json.WriteNumberValue(node.WaitTime);
        json.WriteNumberValue(node.Samples ?? 0);
        json.WriteStartArray();
        foreach (CallTreeNode child in node.Children.OrderByDescending(static c => c.TotalTime))
            WriteNode(json, child, id);
        json.WriteEndArray();
        json.WriteEndArray();
    }

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>CPU profile · __TITLE__</title>
        <style>
        :root {
          --bg: #f7f7f5; --panel: #ffffff; --text: #1d1d1b; --muted: #6b6b66; --line: #e3e2dd; --accent: #2f6fdf;
          --dim: 0.25; --wait: #c9c8c2; --hl: #ffcf33;
          --c-nm: 28; --c-sys: 212; --c-ms: 170; --c-other: 280; --sat: 62%; --lit: 68%;
        }
        @media (prefers-color-scheme: dark) {
          :root { --bg: #141413; --panel: #1e1e1c; --text: #ecebe6; --muted: #9b9a94; --line: #34332f; --accent: #7aa7ff;
                  --wait: #4a4945; --hl: #e0b400; --sat: 45%; --lit: 42%; }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1400px; margin: 0 auto; padding: 24px 16px 48px; }
        h1 { font-size: 22px; margin: 0 0 4px; }
        h2 { font-size: 16px; margin: 0 0 12px; }
        .meta { color: var(--muted); font-size: 13px; word-break: break-all; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 12px; margin: 20px 0; }
        .card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; }
        .card .v { font-size: 20px; font-weight: 600; font-variant-numeric: tabular-nums; }
        .card .k { color: var(--muted); font-size: 12px; }
        section { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 16px; margin-bottom: 16px; }
        .controls { display: flex; flex-wrap: wrap; gap: 12px 20px; align-items: center; margin-bottom: 12px; }
        .controls input[type=search] { flex: 1 1 260px; padding: 6px 10px; border: 1px solid var(--line); border-radius: 6px; background: var(--bg); color: var(--text); font: inherit; }
        .controls label { display: inline-flex; gap: 6px; align-items: center; cursor: pointer; }
        .note { color: var(--muted); font-size: 12px; margin-top: 8px; }
        .crumbs { font-size: 12px; margin-bottom: 8px; display: flex; flex-wrap: wrap; gap: 4px; }
        .crumbs button { border: 0; background: none; color: var(--accent); padding: 0; font: inherit; cursor: pointer; }
        .crumbs span { color: var(--muted); }
        #flame-wrap { position: relative; overflow-x: hidden; }
        #flame { display: block; width: 100%; cursor: pointer; }
        #tip { position: fixed; pointer-events: none; background: var(--panel); border: 1px solid var(--line); border-radius: 6px;
               padding: 6px 8px; font-size: 12px; max-width: 560px; box-shadow: 0 4px 16px rgba(0,0,0,.15); display: none; z-index: 2; word-break: break-all; }
        #detail { font-size: 13px; margin-top: 10px; min-height: 20px; word-break: break-all; }
        table { width: 100%; border-collapse: collapse; font-size: 13px; }
        th, td { padding: 5px 8px; border-bottom: 1px solid var(--line); text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
        th { cursor: pointer; user-select: none; color: var(--muted); font-weight: 600; position: sticky; top: 0; background: var(--panel); }
        th:last-child, td:last-child { text-align: left; white-space: normal; word-break: break-all; }
        td:last-child { cursor: pointer; }
        tr.wait td { color: var(--muted); }
        .bar { display: inline-block; height: 8px; background: var(--accent); border-radius: 2px; vertical-align: middle; margin-right: 6px; opacity: .7; }
        .scroll { max-height: 560px; overflow: auto; }
        </style>
        </head>
        <body>
        <main>
          <h1>CPU profile</h1>
          <div class="meta" id="meta"></div>
          <div class="cards" id="cards"></div>
          <div class="controls">
            <input type="search" id="search" placeholder="Search methods (regex), e.g. BlockProcessor|TransactionProcessor">
            <label><input type="checkbox" id="hide-waits" checked> Hide blocking waits</label>
            <label><input type="checkbox" id="hide-pseudo"> Hide profiler pseudo-frames</label>
          </div>
          <section>
            <h2>Flame graph</h2>
            <div class="crumbs" id="crumbs"></div>
            <div id="flame-wrap"><canvas id="flame"></canvas></div>
            <div id="detail"></div>
            <div class="note" id="flame-note"></div>
          </section>
          <section>
            <h2>Hotspots</h2>
            <div class="scroll"><table id="table"><thead><tr>
              <th data-k="1">Own ms</th><th data-k="p">Own %</th><th data-k="2">Total ms</th><th data-k="3">Samples</th><th data-k="0">Method</th>
            </tr></thead><tbody></tbody></table></div>
            <div class="note">The <span id="row-count"></span> methods with the most own time. Click a method to find it in the flame graph.</div>
          </section>
        </main>
        <div id="tip"></div>
        <script id="data" type="application/json">__DATA__</script>
        <script>
        (() => {
        const D = JSON.parse(document.getElementById('data').textContent);
        const N = D.names;
        const fmt = (v, d = 1) => v.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
        const pct = (v, t) => t > 0 ? (100 * v / t).toFixed(1) + ' %' : '-';
        const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
        const $ = id => document.getElementById(id);

        // Tree node: [name, total, own, wait, samples, children]; parent links are added for breadcrumbs.
        (function link(n, p) { n.parent = p; for (const c of n[5]) link(c, n); })(D.tree, null);

        // n.x is the part of a node's time that the active filters hide: blocking waits and pseudo-frames such as [Native or optimized code].
        const waitIds = new Set(D.waits), isPseudo = id => N[id].startsWith('[');
        let hideWaits = true, hidePseudo = false, search = null, zoom = D.tree;
        function recompute() {
          (function hidden(n) {
            let x = 0;
            for (const c of n[5]) x += hidden(c);
            if ((hideWaits && waitIds.has(n[0])) || (hidePseudo && isPseudo(n[0]))) x = n[1];
            return n.x = x;
          })(D.tree);
        }
        const value = n => n[1] - n.x;

        $('meta').textContent = `${D.snapshot} · captured ${D.captured} · report generated ${D.generated}`;
        const active = D.totalTime - D.waitTime;
        const pseudo = D.tree[5].filter(c => isPseudo(c[0])).reduce((s, c) => s + c[1], 0);
        // Pseudo-frames start hidden when they would squeeze the managed call tree into slivers.
        hidePseudo = $('hide-pseudo').checked = pseudo > active / 2;
        $('cards').innerHTML = [
          [fmt(D.totalTime / 1000) + ' s', 'Sampled time, all threads'],
          [pct(D.waitTime, D.totalTime), 'Blocked in waits'],
          [fmt(active / 1000) + ' s', 'Time outside waits'],
          [pct(pseudo, active), 'Of that in pseudo-frames'],
          [D.methods.toLocaleString('en-US'), 'Methods'],
          [D.nodes.toLocaleString('en-US'), 'Call-tree nodes shown'],
        ].map(([v, k]) => `<div class="card"><div class="v">${v}</div><div class="k">${k}</div></div>`).join('');
        $('flame-note').textContent =
          `Times are wall-clock milliseconds summed over all threads. ${D.nodesBeforePruning - D.nodes} of ${D.nodesBeforePruning} call paths are too small to draw and are omitted. ` +
          `Bracketed frames such as [Native or optimized code] are profiler pseudo-frames; on Linux they also hold time that native threads spend blocked, ` +
          `so they start hidden when they exceed half of the time outside waits.`;

        // Flame graph (icicle: callers on top).
        const canvas = $('flame'), ctx = canvas.getContext('2d'), tip = $('tip');
        const ROW = 18;
        let rects = [];
        const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
        function hue(name, isRoot) {
          if (isRoot || name.startsWith('[')) return null;
          if (name.startsWith('Nethermind.')) return +css('--c-nm');
          if (name.startsWith('System.')) return +css('--c-sys');
          if (name.startsWith('Microsoft.')) return +css('--c-ms');
          return +css('--c-other');
        }
        function shortName(name) {
          const generic = name.indexOf('`'), cut = generic > 0 ? name.slice(0, generic) : name;
          const parts = cut.split('.');
          return parts.length > 2 ? parts.slice(-2).join('.') + name.slice(cut.length) : name;
        }
        function depthOf(n) { let d = 0; for (const c of n[5]) if (value(c) > 0) d = Math.max(d, depthOf(c)); return d + 1; }
        function draw() {
          const width = canvas.parentElement.clientWidth, dpr = window.devicePixelRatio || 1;
          const height = depthOf(zoom) * ROW;
          canvas.width = width * dpr; canvas.height = height * dpr; canvas.style.height = height + 'px';
          ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
          ctx.font = '11px system-ui, sans-serif'; ctx.textBaseline = 'middle';
          rects = [];
          const sat = css('--sat'), lit = css('--lit'), wait = css('--wait'), hl = css('--hl'), text = css('--text');
          const total = value(zoom);
          (function walk(n, x, depth, matchedAbove) {
            const w = total > 0 ? value(n) / total * width : 0;
            if (w < 0.5) return;
            const name = N[n[0]], matched = matchedAbove || (search && search.test(name));
            const h = hue(name, n === D.tree), y = depth * ROW;
            ctx.globalAlpha = search && !matched ? +css('--dim') : 1;
            ctx.fillStyle = search && matched && !matchedAbove ? hl
              : waitIds.has(n[0]) || h === null ? wait : `hsl(${h + (n[0] * 37 % 24) - 12} ${sat} ${lit})`;
            ctx.fillRect(x, y, Math.max(w - 1, 0.5), ROW - 1);
            if (w > 40) {
              ctx.fillStyle = text;
              let label = shortName(name);
              const max = w - 8;
              if (ctx.measureText(label).width > max) {
                while (label.length > 1 && ctx.measureText(label + '…').width > max) label = label.slice(0, -1);
                label += '…';
              }
              ctx.fillText(label, x + 4, y + ROW / 2);
            }
            ctx.globalAlpha = 1;
            rects.push([x, y, w, n]);
            let cx = x;
            for (const c of n[5]) { walk(c, cx, depth + 1, matched); cx += total > 0 ? value(c) / total * width : 0; }
          })(zoom, 0, 0, ancestors(zoom).some(n => search?.test(N[n[0]])));
          crumbs();
        }
        function ancestors(n) {
          const path = [];
          for (let p = n.parent; p; p = p.parent) path.unshift(p);
          return path;
        }
        function crumbs() {
          const path = [...ancestors(zoom), zoom];
          $('crumbs').innerHTML = path.map((n, i) => i === path.length - 1
            ? `<span>${esc(shortName(N[n[0]]))}</span>` : `<button data-i="${i}">${esc(shortName(N[n[0]]))}</button><span>›</span>`).join('');
          $('crumbs').querySelectorAll('button').forEach(b => b.onclick = () => { zoom = path[+b.dataset.i]; draw(); });
        }
        function hit(e) {
          const r = canvas.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
          for (let i = rects.length - 1; i >= 0; i--) {
            const [rx, ry, rw, n] = rects[i];
            if (x >= rx && x < rx + rw && y >= ry && y < ry + ROW) return n;
          }
          return null;
        }
        function describe(n) {
          const root = value(D.tree);
          return `<b>${esc(N[n[0]])}</b><br>total ${fmt(value(n))} ms (${pct(value(n), root)} of all, ${pct(value(n), value(zoom))} of view)` +
            ` · own ${fmt(n[2])} ms` + (n[4] ? ` · ${n[4].toLocaleString('en-US')} samples` : '') +
            (n.x > 0 ? ` · ${fmt(n.x)} ms hidden by filters` : '');
        }
        canvas.onmousemove = e => {
          const n = hit(e);
          if (!n) { tip.style.display = 'none'; return; }
          tip.innerHTML = describe(n);
          tip.style.display = 'block';
          const left = Math.min(e.clientX + 14, window.innerWidth - tip.offsetWidth - 8);
          tip.style.left = left + 'px'; tip.style.top = (e.clientY + 14) + 'px';
        };
        canvas.onmouseleave = () => tip.style.display = 'none';
        canvas.onclick = e => { const n = hit(e); if (n) { zoom = n; $('detail').innerHTML = describe(n); draw(); } };

        // Hotspot table.
        let sortKey = 1, sortDesc = true;
        function table() {
          const basis = value(D.tree);
          let rows = D.functions.filter(f => !(hideWaits && f[4]) && !(hidePseudo && isPseudo(f[0])) && (!search || search.test(N[f[0]])));
          const key = sortKey === 'p' ? 1 : sortKey;
          rows.sort((a, b) => (key === 0 ? N[a[0]].localeCompare(N[b[0]]) : a[key] - b[key]) * (sortDesc ? -1 : 1));
          const top = Math.max(...rows.map(f => f[1]), 1);
          $('table').tBodies[0].innerHTML = rows.map(f =>
            `<tr class="${f[4] ? 'wait' : ''}"><td><span class="bar" style="width:${Math.round(60 * f[1] / top)}px"></span>${fmt(f[1])}</td>` +
            `<td>${pct(f[1], basis)}</td><td>${fmt(f[2])}</td><td>${f[3].toLocaleString('en-US')}</td><td data-n="${f[0]}">${esc(N[f[0]])}</td></tr>`).join('');
          $('row-count').textContent = rows.length;
        }
        $('table').tHead.onclick = e => {
          const k = e.target.dataset.k;
          if (k === undefined) return;
          const key = k === 'p' || k === '0' ? (k === 'p' ? 'p' : 0) : +k;
          sortDesc = sortKey === key ? !sortDesc : key !== 0;
          sortKey = key;
          table();
        };
        $('table').tBodies[0].onclick = e => {
          const id = e.target.closest('td')?.dataset.n;
          if (id === undefined) return;
          const name = N[+id];
          $('search').value = '^' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$';
          applySearch();
          canvas.scrollIntoView({ behavior: 'smooth', block: 'center' });
        };

        function applySearch() {
          const q = $('search').value.trim();
          try { search = q ? new RegExp(q, 'i') : null; $('search').style.borderColor = ''; }
          catch { search = null; $('search').style.borderColor = 'crimson'; }
          if (search) {
            let matched = 0;
            (function sum(n) { if (search.test(N[n[0]])) { matched += value(n); return; } n[5].forEach(sum); })(D.tree);
            $('detail').innerHTML = `Matching frames: ${fmt(matched)} ms, ${pct(matched, value(D.tree))} of all shown time.`;
          } else {
            $('detail').textContent = '';
          }
          draw(); table();
        }
        $('search').oninput = applySearch;
        $('hide-waits').onchange = e => { hideWaits = e.target.checked; recompute(); applySearch(); };
        $('hide-pseudo').onchange = e => { hidePseudo = e.target.checked; recompute(); applySearch(); };
        window.addEventListener('resize', draw);
        recompute(); draw(); table();
        })();
        </script>
        </body>
        </html>
        """;
}
