// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';
// ----------------------------------------------------------
//  Create a pie chart once on page load
//  Then update it whenever SSE data arrives
// ----------------------------------------------------------

// 1) Chart dimensions
const pieDiameter = 100;         // width & height for the *pie itself*
const labelArea = 120;           // extra space on the right for labels
const svgWidth = pieDiameter + labelArea;
const svgHeight = pieDiameter;
const radius = pieDiameter / 2;

// 2) Create SVG & group
const svg = d3
  .select("#pie-chart")
  .append("svg")
  .attr("width", svgWidth)
  .attr("height", svgHeight)
  // allow labels to overflow if you tweak beyond labelArea
  .attr("overflow", "visible");

const chartGroup = svg
  .append("g")
  // center the pie in the *first* pieDiameter pixels
  .attr("transform", `translate(${pieDiameter / 2}, ${pieDiameter / 2})`);

// Color scale (you can change to your own palette)
const color = d3
  .scaleOrdinal<string>()
  .range(d3.schemeTableau10);

// Pie & Arc generators
const pie = d3
  .pie<{ type: string; count: number }>()
  .sort(null) // keep slices in the original order
  .value((d) => d.count);

const arc = d3
  .arc<d3.PieArcDatum<{ type: string; count: number }>>()
  .innerRadius(0)
  .outerRadius(radius);

// sliceLayer holds only the 'path's
const sliceLayer = chartGroup.append("g").attr("class", "slice-layer");
// labelLayer holds all the callout lines + texts
const labelLayer = chartGroup.append("g").attr("class", "label-layer");

export function updatePieChart(data: { type: string; count: number }[]) {
  // 1) Build the pie arcs
  const pieData = pie(data);

  // 2) Augment each arc with its centroid Y
  interface Aug extends d3.PieArcDatum<{ type: string; count: number }> {
    centroidY: number;
  }
  const withCentroid: Aug[] = pieData.map(d => {
    const [cx, cy] = arc.centroid(d);
    return { ...d, centroidY: cy };
  });

  // 3) Sort by centroidY so top‑most slices label first
  const stack = withCentroid
    .slice()
    .sort((a, b) => a.centroidY - b.centroidY);

  // 4) Compute stacked label positions
  const lineHeight = 18;
  const offsetY = -((stack.length - 1) * lineHeight) / 2;
  const labelX = radius + 20;    // still all on the right
  const labelPos: Record<string, { x: number; y: number }> = {};
  stack.forEach((d, i) => {
    labelPos[d.data.type] = {
      x: labelX,
      y: offsetY + i * lineHeight
    };
  });

  // ---- SLICES LAYER ----
  const paths = sliceLayer
    .selectAll<SVGPathElement, typeof pieData[0]>("path")
    .data(pieData, d => d.data.type);

  paths.exit().remove();

  const pathsEnter = paths.enter()
    .append("path")
    .attr("stroke", "#222")
    .style("stroke-width", "1px")
    .attr("fill", d => color(d.data.type))
    .each(function (d) { (this as any)._current = d; });

  pathsEnter.merge(paths)
    .transition().duration(750)
    .attrTween("d", function (d) {
      const interp = d3.interpolate((this as any)._current, d);
      (this as any)._current = interp(0);
      return t => arc(interp(t))!;
    });

  // ---- LABELS LAYER ----
  // One <g> per slice for polyline+text
  const callouts = labelLayer
    .selectAll<SVGGElement, Aug>("g.label")
    .data(withCentroid, d => d.data.type);

  callouts.exit().remove();

  const enter = callouts.enter()
    .append("g")
    .attr("class", "label")
    .style("pointer-events", "none");

  enter.append("polyline")
    .attr("fill", "none")
    .attr("stroke", "#fff");

  enter.append("text")
    .style("alignment-baseline", "middle")
    .style("text-anchor", "start")
    .style("fill", "#fff");

  const all = enter.merge(callouts);

  // 5) Animate the lines
  all.select("polyline")
    .transition().duration(750)
    .attr("points", d => {
      const [cx, cy] = arc.centroid(d);
      const { x: px, y: py } = labelPos[d.data.type];
      const mx = px * 0.6;
      return `${cx},${cy} ${mx},${py} ${px},${py}`;
    });

  // 6) Animate the texts
  all.select("text")
    .transition().duration(750)
    .attr("transform", d => {
      const p = labelPos[d.data.type];
      return `translate(${p.x},${p.y})`;
    })
    .tween("label", function (d) {
      return () => { (this as any).textContent = `${d.data.type} (${d.data.count})`; };
    });
}
