// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;

namespace Nethermind.Tools.ProfilingMcp.Native;

/// <summary>Renders a process's memory breakdown as a self-contained HTML page with a zoomable treemap and a category table.</summary>
/// <remarks>
/// The page embeds its data and script and loads nothing, so it opens offline and can be shared as a single file.
/// The JSON is written with the default encoder, which escapes <c>&lt;</c>, so file and thread names cannot close the script element.
/// </remarks>
internal static class MemoryReport
{
    /// <summary>Items listed under one category; the smaller ones are merged into one tile.</summary>
    private const int MaxItems = 60;

    /// <summary>The page's top level, in display order; each category belongs to the first group whose test it passes.</summary>
    internal static readonly (string Name, Func<string, bool> Contains)[] Groups =
    [
        ("Managed heap (GC)", static c => c.StartsWith(NativeMemory.GcPrefix, StringComparison.Ordinal)),
        ("Native heap", static c => c is NativeMemory.MallocArenas or NativeMemory.MallocMain or NativeMemory.MallocMmapped or NativeMemory.OtherAnonymous),
        (".NET runtime", static c => c.StartsWith(NativeMemory.RuntimePrefix, StringComparison.Ordinal)),
        ("Thread stacks", static c => c == NativeMemory.ThreadStacks),
        ("Mapped files", static c => c.StartsWith("files: ", StringComparison.Ordinal)),
        ("Other", static _ => true),
    ];

    public static string Render(string title, IReadOnlyList<MemoryShare> shares, long? gcCommitted)
    {
        using MemoryStream body = new();
        using (Utf8JsonWriter json = new(body))
        {
            json.WriteStartObject();
            json.WriteString("title", title);
            json.WriteString("generated", DateTime.UtcNow.ToString("u"));
            json.WriteNumber("swap", (long)shares.Sum(static s => s.Swap));
            if (gcCommitted is { } committed)
                json.WriteNumber("gcCommitted", committed);

            json.WriteStartArray("groups");
            foreach ((string name, _) in Groups)
                json.WriteStringValue(name);
            json.WriteEndArray();

            // A node is [name, bytes, group, children].
            json.WritePropertyName("tree");
            json.WriteStartArray();
            json.WriteStringValue("Resident memory");
            json.WriteNumberValue((long)shares.Sum(static s => s.Rss));
            json.WriteNumberValue(-1);
            json.WriteStartArray();
            foreach (IGrouping<int, MemoryShare> group in shares.GroupBy(static s => GroupOf(s.Category)).OrderBy(static g => g.Key))
            {
                WriteNode(json, Groups[group.Key].Name, group.Sum(static s => s.Rss), group.Key, () =>
                {
                    foreach (IGrouping<string, MemoryShare> category in group.GroupBy(static s => s.Category).OrderByDescending(static c => c.Sum(static s => s.Rss)))
                        WriteNode(json, category.Key, category.Sum(static s => s.Rss), group.Key, () => WriteItems(json, category, group.Key));
                });
            }
            json.WriteEndArray();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Template
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(title), StringComparison.Ordinal)
            .Replace("__DATA__", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    internal static int GroupOf(string category) => Array.FindIndex(Groups, g => g.Contains(category));

    private static void WriteItems(Utf8JsonWriter json, IEnumerable<MemoryShare> shares, int group)
    {
        (string Item, double Rss)[] items = [.. shares.GroupBy(static s => s.Item).Select(static i => (i.Key, i.Sum(static s => s.Rss))).Where(static i => i.Item2 >= 1).OrderByDescending(static i => i.Item2)];
        if (items.Length <= 1)
            return;

        foreach ((string item, double rss) in items.Take(MaxItems))
            WriteNode(json, item, rss, group, null);
        if (items.Length > MaxItems)
            WriteNode(json, $"{items.Length - MaxItems:N0} smaller items", items.Skip(MaxItems).Sum(static i => i.Rss), group, null);
    }

    private static void WriteNode(Utf8JsonWriter json, string name, double bytes, int group, Action? children)
    {
        json.WriteStartArray();
        json.WriteStringValue(name);
        json.WriteNumberValue((long)bytes);
        json.WriteNumberValue(group);
        json.WriteStartArray();
        children?.Invoke();
        json.WriteEndArray();
        json.WriteEndArray();
    }

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Memory · __TITLE__</title>
        <style>
        :root {
          --bg: #f7f7f5; --panel: #ffffff; --text: #1d1d1b; --muted: #6b6b66; --line: #e3e2dd; --accent: #2f6fdf;
          --sat: 58%; --lit: 70%; --lit-inner: 80%; --tile-text: #1d1d1b;
        }
        @media (prefers-color-scheme: dark) {
          :root { --bg: #141413; --panel: #1e1e1c; --text: #ecebe6; --muted: #9b9a94; --line: #34332f; --accent: #7aa7ff;
                  --sat: 40%; --lit: 36%; --lit-inner: 27%; --tile-text: #ecebe6; }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1400px; margin: 0 auto; padding: 24px 16px 48px; }
        h1 { font-size: 22px; margin: 0 0 4px; }
        h2 { font-size: 16px; margin: 0 0 12px; }
        .meta { color: var(--muted); font-size: 13px; word-break: break-all; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; margin: 20px 0 12px; }
        .card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; }
        .card .v { font-size: 20px; font-weight: 600; font-variant-numeric: tabular-nums; }
        .card .k { color: var(--muted); font-size: 12px; display: flex; gap: 6px; align-items: center; }
        .swatch { width: 10px; height: 10px; border-radius: 2px; flex: none; }
        .stack { display: flex; height: 14px; border-radius: 7px; overflow: hidden; margin: 0 0 20px; border: 1px solid var(--line); }
        .stack div { height: 100%; }
        section { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 16px; margin-bottom: 16px; }
        .note { color: var(--muted); font-size: 12px; margin-top: 8px; }
        .crumbs { font-size: 12px; margin-bottom: 8px; display: flex; flex-wrap: wrap; gap: 4px; }
        .crumbs button { border: 0; background: none; color: var(--accent); padding: 0; font: inherit; cursor: pointer; }
        .crumbs span { color: var(--muted); }
        #map { position: relative; width: 100%; height: 560px; }
        .tile { position: absolute; overflow: hidden; border: 1px solid var(--panel); color: var(--tile-text); font-size: 12px; cursor: pointer; }
        .tile .h { padding: 2px 5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font-weight: 600; }
        .tile .h small { font-weight: 400; opacity: .75; }
        .tile .inner { position: absolute; }
        .tile .leaf { position: absolute; overflow: hidden; border: 1px solid var(--panel); padding: 1px 4px; white-space: nowrap; text-overflow: ellipsis; font-size: 11px; }
        #tip { position: fixed; pointer-events: none; background: var(--panel); border: 1px solid var(--line); border-radius: 6px;
               padding: 6px 8px; font-size: 12px; max-width: 520px; box-shadow: 0 4px 16px rgba(0,0,0,.15); display: none; z-index: 2; word-break: break-all; }
        table { width: 100%; border-collapse: collapse; font-size: 13px; }
        th, td { padding: 5px 8px; border-bottom: 1px solid var(--line); text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
        th { color: var(--muted); font-weight: 600; }
        th:last-child, td:last-child { text-align: left; white-space: normal; }
        td:last-child { cursor: pointer; }
        .bar { display: inline-block; height: 8px; border-radius: 2px; vertical-align: middle; margin-right: 6px; }
        </style>
        </head>
        <body>
        <main>
          <h1>Memory breakdown</h1>
          <div class="meta" id="meta"></div>
          <div class="cards" id="cards"></div>
          <div class="stack" id="stack"></div>
          <section>
            <h2>Resident memory map</h2>
            <div class="crumbs" id="crumbs"></div>
            <div id="map"></div>
            <div class="note">Area is resident bytes. Click a tile to zoom in; use the path above to zoom out.</div>
          </section>
          <section>
            <h2>Categories</h2>
            <table id="table"><thead><tr><th>Resident</th><th>% of RSS</th><th>Items</th><th>Category</th></tr></thead><tbody></tbody></table>
            <div class="note">Anonymous mappings are split by address; one that is only partly a known range is split in proportion to its bytes, so small categories are estimates.
              Allocators keep freed memory, so malloc and other native heap RSS is a high-water mark rather than what is in use.</div>
          </section>
        </main>
        <div id="tip"></div>
        <script id="data" type="application/json">__DATA__</script>
        <script>
        (() => {
        const D = JSON.parse(document.getElementById('data').textContent);
        const $ = id => document.getElementById(id);
        const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
        const size = b => b >= 2 ** 30 ? (b / 2 ** 30).toFixed(2) + ' GB' : b >= 2 ** 20 ? (b / 2 ** 20).toFixed(1) + ' MB' : (b / 1024).toFixed(1) + ' KB';
        const pct = (v, t) => t > 0 ? (100 * v / t).toFixed(1) + ' %' : '-';
        const hues = [212, 28, 170, 280, 120, 0];
        const color = (g, inner) => g < 0 ? 'var(--line)' : `hsl(${hues[g]} ${g === 5 ? '0%' : 'var(--sat)'} var(${inner ? '--lit-inner' : '--lit'}))`;

        // Node: [name, bytes, group, children]; parent links are added for the path.
        (function link(n, p) { n.parent = p; for (const c of n[3]) link(c, n); })(D.tree, null);
        const total = D.tree[1];

        $('meta').textContent = `${D.title} · generated ${D.generated}`;
        const cards = [['Resident (RSS)', total, -1]].concat(D.tree[3].filter(g => g[1] >= total / 1000).map(g => [g[0], g[1], g[2]]));
        if (D.gcCommitted !== undefined) cards.push(['GC heap committed', D.gcCommitted, -1]);
        if (D.swap > 0) cards.push(['Swapped out', D.swap, -1]);
        $('cards').innerHTML = cards.map(([k, v, g]) =>
          `<div class="card"><div class="k">${g >= 0 ? `<span class="swatch" style="background:${color(g)}"></span>` : ''}${esc(k)}</div>` +
          `<div class="v">${size(v)}</div>${g >= 0 ? `<div class="k">${pct(v, total)} of RSS</div>` : ''}</div>`).join('');
        $('stack').innerHTML = D.tree[3].map(g => `<div title="${esc(g[0])}: ${size(g[1])}" style="width:${100 * g[1] / total}%;background:${color(g[2])}"></div>`).join('');

        // Squarified treemap: lays children out in rows whose tiles stay close to square.
        function squarify(items, x, y, w, h) {
          const out = [], sum = items.reduce((s, n) => s + n[1], 0);
          if (sum <= 0 || w <= 0 || h <= 0) return out;
          const scale = w * h / sum;
          const worst = (row, side) => {
            const s = row.reduce((a, n) => a + n[1], 0) * scale, max = row[0][1] * scale, min = row[row.length - 1][1] * scale;
            return Math.max(side * side * max / (s * s), s * s / (side * side * min));
          };
          let rest = items.filter(n => n[1] > 0);
          while (rest.length) {
            const side = Math.min(w, h);
            let row = [rest[0]], i = 1;
            while (i < rest.length && worst(row.concat([rest[i]]), side) <= worst(row, side)) row.push(rest[i++]);
            rest = rest.slice(i);
            const area = row.reduce((a, n) => a + n[1], 0) * scale;
            if (w >= h) {
              const cw = area / h; let cy = y;
              for (const n of row) { const rh = n[1] * scale / cw; out.push([n, x, cy, cw, rh]); cy += rh; }
              x += cw; w -= cw;
            } else {
              const rh = area / w; let cx = x;
              for (const n of row) { const rw = n[1] * scale / rh; out.push([n, cx, y, rw, rh]); cx += rw; }
              y += rh; h -= rh;
            }
          }
          return out;
        }

        const sorted = n => n[3].slice().sort((a, b) => b[1] - a[1]);
        const pathOf = n => { const p = []; for (let x = n; x; x = x.parent) p.unshift(x); return p; };
        const describe = n => `<b>${esc(n[0])}</b><br>${size(n[1])} · ${pct(n[1], total)} of RSS` +
          (n.parent && n.parent !== D.tree ? ` · ${pct(n[1], n.parent[1])} of ${esc(n.parent[0])}` : '');
        let zoom = D.tree;

        function draw() {
          const map = $('map'), W = map.clientWidth, H = map.clientHeight;
          map.innerHTML = '';
          for (const [n, x, y, w, h] of squarify(sorted(zoom), 0, 0, W, H)) {
            const tile = document.createElement('div');
            tile.className = 'tile';
            Object.assign(tile.style, { left: x + 'px', top: y + 'px', width: w + 'px', height: h + 'px', background: color(n[2]) });
            tile.node = n;
            if (h >= 16 && w >= 40) tile.innerHTML = `<div class="h">${esc(n[0])} <small>${size(n[1])}</small></div>`;
            if (n[3].length && w > 80 && h > 50) {
              const inner = document.createElement('div');
              inner.className = 'inner';
              Object.assign(inner.style, { left: '2px', top: '20px', width: (w - 6) + 'px', height: (h - 24) + 'px' });
              for (const [c, cx, cy, cw, ch] of squarify(sorted(n), 0, 0, w - 6, h - 24)) {
                const leaf = document.createElement('div');
                leaf.className = 'leaf';
                Object.assign(leaf.style, { left: cx + 'px', top: cy + 'px', width: cw + 'px', height: ch + 'px', background: color(c[2], true) });
                if (ch >= 14 && cw >= 50) leaf.textContent = c[0];
                leaf.node = c;
                inner.appendChild(leaf);
              }
              tile.appendChild(inner);
            }
            map.appendChild(tile);
          }
          $('crumbs').innerHTML = pathOf(zoom).map((n, i, a) => i === a.length - 1
            ? `<span>${esc(n[0])} (${size(n[1])})</span>` : `<button data-i="${i}">${esc(n[0])}</button><span>›</span>`).join('');
        }

        $('crumbs').addEventListener('click', e => { const i = e.target.dataset.i; if (i !== undefined) { zoom = pathOf(zoom)[+i]; draw(); } });
        $('map').addEventListener('click', e => {
          // A click on an item zooms to its category, so the item is shown with its neighbours.
          let n = e.target.closest('.leaf, .tile')?.node;
          if (!n) return;
          if (!n[3].length) n = n.parent;
          if (n && n !== zoom && n[3].length) { zoom = n; draw(); }
        });
        const tip = $('tip');
        $('map').addEventListener('mousemove', e => {
          const n = e.target.closest('.leaf, .tile')?.node;
          if (!n) { tip.style.display = 'none'; return; }
          tip.innerHTML = describe(n);
          tip.style.display = 'block';
          tip.style.left = Math.min(e.clientX + 12, innerWidth - tip.offsetWidth - 8) + 'px';
          tip.style.top = Math.min(e.clientY + 12, innerHeight - tip.offsetHeight - 8) + 'px';
        });
        $('map').addEventListener('mouseleave', () => tip.style.display = 'none');

        const categories = D.tree[3].flatMap(g => g[3]).sort((a, b) => b[1] - a[1]);
        $('table').tBodies[0].innerHTML = categories.map((c, i) =>
          `<tr><td>${size(c[1])}</td><td>${pct(c[1], total)}</td><td>${c[3].length || 1}</td>` +
          `<td data-i="${i}"><span class="bar" style="width:${Math.max(2, 120 * c[1] / categories[0][1])}px;background:${color(c[2])}"></span>${esc(c[0])}</td></tr>`).join('');
        $('table').addEventListener('click', e => {
          const i = e.target.closest('td[data-i]')?.dataset.i;
          if (i !== undefined) { zoom = categories[+i][3].length ? categories[+i] : categories[+i].parent; draw(); $('map').scrollIntoView({ behavior: 'smooth', block: 'center' }); }
        });

        addEventListener('resize', draw);
        draw();
        })();
        </script>
        </body>
        </html>
        """;
}
