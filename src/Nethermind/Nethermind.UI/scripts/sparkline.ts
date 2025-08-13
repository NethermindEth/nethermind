// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';
export interface Datum {
  t: number; // e.g., timestamp in ms or any ascending numeric index
  v: number; // the actual numeric value
}
interface Margin {
  top: number;
  right: number;
  bottom: number;
  left: number;
}

/**
 * A sparkline that slides left as new data arrives.
 * Uses d3.scaleTime() with t in milliseconds.
 *
 * @param element   Container element (like a <div>) for the sparkline.
 * @param data      Array of {t, v} in ascending order by t (ms).
 * @param newDatum  A new data point { t, v } to add.
 * @param width     Outer width of the sparkline SVG (default 300).
 * @param height    Outer height of the sparkline SVG (default 60).
 * @param maxPoints Maximum points in the rolling window (default 60).
 */
export function sparkline(
  element: HTMLElement,
  data: Datum[],
  width = 80,
  height = 44,
  maxPoints = 60
) {
  const newDatum: Datum = data[data.length - 1];
  //
  // 1. Push the new datum, filter to the last `maxPoints` seconds
  //
  const leftEdge = newDatum.t - maxPoints * 1000;
  // Keep only data within the fixed time window
  data = data.filter(d => d.t >= leftEdge);

  //
  // 2. Define margins and compute inner dimensions
  //
  const margin: Margin = { top: 2, right: 2, bottom: 2, left: 2 };
  const innerWidth = width - margin.left - margin.right;
  const innerHeight = height - margin.top - margin.bottom;

  //
  // 3. Create or select the <svg> and "line-group"
  //
  let svg = d3.select(element).select<SVGSVGElement>('svg');
  if (svg.empty()) {
      svg = d3.select(element)
        .append('svg')
        .attr('width', width)
        .attr('height', height);

      // Group for line path:
      svg.append('g')
        .attr('class', 'line-group')
        .attr('transform', `translate(${margin.left},${margin.top})`)
        .append('path')
        .attr('class', 'sparkline-path')
        .attr('fill', 'none')
        .attr('stroke', '#00bff2')
        .attr('stroke-width', 1.5);

      // Group for y-axis (one-time creation):
      svg.append('g')
        .attr('class', 'y-axis')
        .attr('transform', `translate(${margin.left},${margin.top})`);
  }

  // In case width/height changed
  svg.attr('width', width).attr('height', height);

  const lineGroup = svg.select<SVGGElement>('g.line-group');
  const path = lineGroup.select<SVGPathElement>('path.sparkline-path');

  //
  // 4. Build x-scale with a fixed time window [now - maxPoints*1000, now]
  //
  //    Because each point is ~1 second apart and we keep exactly maxPoints seconds,
  //    the domain width is constant => we won't see "jumps."
  //
  const now = newDatum.t;
  const x = d3.scaleTime()
    .domain([new Date(leftEdge), new Date(now)])
    .range([0, innerWidth]);

  //
  // 5. Build a y-scale (either dynamic or fixed).
  //    If your values vary widely, you may see some vertical re-scaling.
  //    For zero vertical shifting, replace with a fixed domain, e.g. [0, 100].
  //
  const [minY, maxY] = d3.extent(data, d => d.v) as [number, number];
  const y = d3.scaleLinear()
    .domain([minY, maxY])
    .range([innerHeight, 0])
    .nice();

  //
  // 6. Line generator for the updated data
  //
  const lineGenerator = d3.line<Datum>()
    .x(d => x(new Date(d.t))!)
    .y(d => y(d.v));

  // Draw the path in its new shape (final position, no transform).
  path.datum(data).attr('d', lineGenerator).attr('transform', null);

  //
  // 7. "Slide" effect:
  //    Because we shift the domain by exactly 1 second each time (maxPoints unchanged),
  //    the horizontal shift in pixels is always innerWidth / maxPoints.
  //
  //    We'll start the path shifted right by that amount, then transition back to 0.
  //
  if (data.length > 1) {
    const xShift = innerWidth / maxPoints; // e.g. 300px wide / 50 = 6px shift

    // Immediately shift path to the right
    path.attr('transform', `translate(${xShift},0)`);

    // Then animate to transform(0,0)
    path.transition()
      .duration(300)
      .attr('transform', 'translate(0,0)');
  }
}
