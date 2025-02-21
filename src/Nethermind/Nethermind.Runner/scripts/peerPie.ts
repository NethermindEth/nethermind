// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';
// ----------------------------------------------------------
//  Create a pie chart once on page load
//  Then update it whenever SSE data arrives
// ----------------------------------------------------------

// Basic chart config
const width = 200;
const height = 100;
const margin = 0;
const radius = Math.min(width, height) / 2 - margin;

// Create an SVG and group for the pie chart
const svg = d3
  .select("#pie-chart")
  .append("svg")
  .attr("width", width)
  .attr("height", height);

// We'll place everything in a group centered in the SVG
const chartGroup = svg
  .append("g")
  .attr("transform", `translate(${width / 4}, ${height / 2})`);

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

// We create a group for each arc (slice + label + arrow)
function keyFn(d: d3.PieArcDatum<{ type: string; count: number }>) {
  // Use the node type as the key
  return d.data.type;
}

// --- UPDATE FUNCTION (WITH TRANSITIONS) ---
export function updatePieChart(data: { type: string; count: number }[]) {
  // 1) Build the pie data (slices)
  const pieData = pie(data);

  // 2) Sort slices ALPHABETICALLY (by their `type`) **just** for labeling
  //    We won't reorder the slices themselves on the chart (that's governed by pieData),
  //    but we *will* decide the label stacking order by alphabetical type.
  const sortedForLabels = pieData
    .slice()
    .sort((a, b) => a.data.type.localeCompare(b.data.type));

  // 3) Assign each slice a "stacked" y-position for its label
  //    so that labels don't overlap
  const lineHeight = 18; // vertical spacing between stacked labels
  const offsetY = -((sortedForLabels.length - 1) * lineHeight) / 2;

  // We'll store the assigned (x, y) in a dictionary keyed by slice's "type"
  const labelX = radius * 1.25; // All labels on the right side
  const labelPositions: Record<string, { x: number; y: number }> = {};

  sortedForLabels.forEach((slice, i) => {
    labelPositions[slice.data.type] = {
      x: labelX,
      y: offsetY + i * lineHeight
    };
  });

  // -- Data Join for the arcs themselves --
  const arcGroups = chartGroup
    .selectAll<SVGGElement, typeof pieData[0]>("g.slice")
    .data(pieData, keyFn);

  // EXIT
  arcGroups.exit().remove();

  // ENTER
  const arcGroupsEnter = arcGroups
    .enter()
    .append("g")
    .attr("class", "slice")
    .each(function (d) {
      // store the initial angles so we can animate from them
      (this as any)._current = d;
    });

  // Each group contains: <path>, <polyline>, <text>
  arcGroupsEnter
    .append("path")
    .attr("fill", (d) => color(d.data.type))
    .attr("stroke", "#222")
    .style("stroke-width", "1px");

  // We'll use a polyline for the kinked callout line
  arcGroupsEnter
    .append("polyline")
    .attr("fill", "none")
    .attr("stroke", "#fff"); // white lines for black background

  // Label text
  arcGroupsEnter
    .append("text")
    .style("fill", "#fff")
    .style("text-anchor", "start")
    .style("alignment-baseline", "middle");

  // MERGE
  const arcGroupsUpdate = arcGroupsEnter.merge(arcGroups);

  // 4) Animate the arc <path>
  arcGroupsUpdate
    .select("path")
    .transition()
    .duration(750)
    .attrTween("d", function (d) {
      const i = d3.interpolate((this as any)._current, d);
      (this as any)._current = i(0);
      return (t) => arc(i(t))!;
    });

  // 5) For each slice, we figure out its final label position from
  //    labelPositions[d.data.type]. Then we draw a polyline
  //    from the slice’s arc centroid to that label, with one “kink.”

  arcGroupsUpdate
    .select("polyline")
    .transition()
    .duration(750)
    .attr("points", (function (d) {
      // Arc centroid
      const [cx, cy] = arc.centroid(d);
      // Stacked label position
      const { x: lx, y: ly } = labelPositions[d.data.type];

      // We'll define:
      //   1) from (cx, cy) horizontally to (radius+10, cy)  [kink corner #1]
      //   2) then vertically to (radius+10, ly)            [kink corner #2]
      //   3) then horizontally to (lx, ly)

      // If you prefer exactly one kink, you can do:
      //   1) from (cx, cy) to (radius+10, ly) [one corner]
      //   2) then (lx, ly)
      // That’s two line segments total, forming one corner.

      const kinkX = radius + 10;

      // Example: single-corner approach
      return [
        [cx, cy],
        [kinkX, ly],
        [lx, ly]
      ];
    }) as any);

  // 6) Animate the text to the stacked position, and set the label text
  arcGroupsUpdate
    .select("text")
    .transition()
    .duration(750)
    .attr("transform", function (d) {
      const { x: lx, y: ly } = labelPositions[d.data.type];
      return `translate(${lx}, ${ly})`;
    })
    .tween("text", function (d) {
      return () => {
        (this as any).textContent = `${d.data.type} (${d.data.count})`;
      };
    });
}
