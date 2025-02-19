// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';
/**
 * Rolling box plot with outlier ignoring, per-tx dots, and a customizable accessor.
 *
 * @param {d3.Selection} selection A d3 selection into which we append an <svg>.
 * @param {Function} accessor     A function(mergedReceipt) => number that selects and scales the numeric value.
 * @param {number} maxHistory     Max number of blocks (boxes) to keep in view.
 *
 * Example accessor:
 *   (d) => parseInt(d.effectiveGasPrice, 16) / 1_000_000
 */
export function createRollingBoxPlot(
  element: HTMLElement,
  accessor: (receipt: any) => number,
  maxHistory = 36
) {
  const margin = { top: 4, right: 4, bottom: 20, left: 20 };
  let width = element.getBoundingClientRect().width;
  const height = 100;

  // Create <svg>
  const svg = d3
    .select(element)
    .append("svg")
    .style("display", "block")
    .style("background", "black")
    .attr("width", width)
    .attr("height", height);

  const g = svg.append("g").attr("transform", `translate(${margin.left},${margin.top})`);

  function getInnerWidth() {
    return width - margin.left - margin.right;
  }
  const innerHeight = height - margin.top - margin.bottom;

  // We'll maintain an array of block data:
  // { xIndex, blockNumber, stats, values[] }
  const blocks: any[] = [];

  // xScale: domain is [0..maxHistory-1], so we have maxHistory "slots".
  const xScale = d3
    .scaleBand<number>()
    .domain(d3.range(maxHistory))
    .range([0, getInnerWidth()])
    .paddingInner(0.3)
    .paddingOuter(0.1);

  // yScale: dynamic domain based on whiskers, fixed range.
  const yScale = d3.scaleLinear().range([innerHeight, 0]);

  function transition() {
    return d3.transition().duration(750);
  }

  // Compute basic box-plot stats with Tukey whiskers
  function computeBoxStats(values: number[]) {
    if (!values.length) {
      return {
        min: 0,
        q1: 0,
        median: 0,
        q3: 0,
        max: 0,
        whiskerLow: 0,
        whiskerHigh: 0,
      };
    }
    const sorted = values.slice().sort(d3.ascending);
    const min = sorted[0];
    const max = sorted[sorted.length - 1];
    const q1 = d3.quantile(sorted, 0.25)!;
    const median = d3.quantile(sorted, 0.5)!;
    const q3 = d3.quantile(sorted, 0.75)!;
    const iqr = q3 - q1;

    const whiskerLow = Math.max(min, q1 - 1.5 * iqr);
    const whiskerHigh = Math.min(max, q3 + 1.5 * iqr);
    return { min, q1, median, q3, max, whiskerLow, whiskerHigh };
  }

  /**
   * update() - called for each new block
   * @param mergedData array of tx receipts
   * @param blockNum numeric block number (or label)
   */
  function update(mergedData: any[], blockNum: number) {
    // 1) Extract data
    const values = mergedData.map(accessor).filter((v) => Number.isFinite(v) && v >= 0);
    const stats = computeBoxStats(values);

    // 2) Placement logic
    if (blocks.length < maxHistory) {
      // Not full yet => place new block at xIndex = blocks.length
      const xIndex = blocks.length;
      blocks.push({
        xIndex,
        blockNumber: blockNum,
        stats,
        values,
      });
    } else {
      // Already at max => shift everything left
      blocks.forEach((b) => {
        b.xIndex -= 1;
      });
      // Remove any that now have xIndex < 0
      while (blocks.length && blocks[0].xIndex < 0) {
        blocks.shift();
      }
      // New block at xIndex = maxHistory - 1
      blocks.push({
        xIndex: maxHistory - 1,
        blockNumber: blockNum,
        stats,
        values,
      });
    }

    // 3) Update y domain ignoring outliers
    const globalWhiskerHigh = d3.max(blocks, (b) => b.stats.whiskerHigh) || 1;
    yScale.domain([0, globalWhiskerHigh]);

    // 4) Data bind
    let boxGroups = g.selectAll<SVGGElement, any>(".box-group").data(blocks, (d: any) => d.xIndex);

    // EXIT
    boxGroups
      .exit()
      .transition(transition())
      .attr("transform", (d: any) => `translate(${xScale(d.xIndex) || 0},${innerHeight}) scale(0.001,0.001)`)
      .remove();

    // ENTER
    const enterGroups = boxGroups
      .enter()
      .append("g")
      .attr("class", "box-group")
      .attr("stroke-width", 1)
      .attr("transform", (d) => `translate(${xScale(d.xIndex) || 0}, ${innerHeight}) scale(0.001,0.001)`);

    // Whisker line
    enterGroups.append("line").attr("class", "whisker-line").attr("stroke", "#aaa");
    // Box
    enterGroups
      .append("rect")
      .attr("class", "box-rect")
      .attr("stroke", "white")
      .attr("fill", "gray")
      .attr("fill-opacity", 1);
    // Median
    enterGroups.append("line").attr("class", "median-line").attr("stroke", "#ccc").attr("stroke-width", 1);
    // Caps
    enterGroups.append("line").attr("class", "whisker-cap lower").attr("stroke", "#aaa");
    enterGroups.append("line").attr("class", "whisker-cap upper").attr("stroke", "#aaa");

    // MERGE
    boxGroups = enterGroups.merge(boxGroups);

    // SHIFT/UPDATE TRANSFORM
    boxGroups
      .transition(transition())
      .attr("transform", (d) => `translate(${xScale(d.xIndex) || 0},0) scale(1,1)`);

    // Render shapes
    boxGroups.each(function (blockData) {
      const sel = d3.select(this);
      const s = blockData.stats;
      const bw = xScale.bandwidth();

      // -------------- COLOR LOGIC BASED ON MEDIAN -------------
      // Find the previous block in `blocks` with xIndex = blockData.xIndex - 1
      let fillColor = "gray"; // default
      const prev = blocks.find((b) => b.xIndex === blockData.xIndex - 1);
      if (prev) {
        if (s.median > prev.stats.median) {
          // Higher median => red tinge
          fillColor = "rgb(127, 63, 63)";
        } else if (s.median < prev.stats.median) {
          // Lower median => green tinge
          fillColor = "rgb(82, 127, 63)";
        } else {
          // Same median => keep it gray
          fillColor = "rgb(127,127,127)";
        }
      }

      // Whisker line
      sel
        .select<SVGLineElement>(".whisker-line")
        .transition(transition())
        .attr("x1", bw / 2)
        .attr("x2", bw / 2)
        .attr("y1", yScale(s.whiskerLow))
        .attr("y2", yScale(s.whiskerHigh));

      sel
        .select<SVGRectElement>(".box-rect")
        .transition(transition())
        .attr("x", 0)
        .attr("width", bw)
        .attr("y", yScale(s.q3))
        .attr("height", Math.max(0, yScale(s.q1) - yScale(s.q3)))
        .attr("fill", fillColor);

      sel
        .select<SVGLineElement>(".median-line")
        .transition(transition())
        .attr("x1", 0)
        .attr("x2", bw)
        .attr("y1", yScale(s.median))
        .attr("y2", yScale(s.median));

      sel
        .select<SVGLineElement>(".whisker-cap.lower")
        .transition(transition())
        .attr("x1", bw * 0.3)
        .attr("x2", bw * 0.7)
        .attr("y1", yScale(s.whiskerLow))
        .attr("y2", yScale(s.whiskerLow));

      sel
        .select<SVGLineElement>(".whisker-cap.upper")
        .transition(transition())
        .attr("x1", bw * 0.3)
        .attr("x2", bw * 0.7)
        .attr("y1", yScale(s.whiskerHigh))
        .attr("y2", yScale(s.whiskerHigh));
    });

    // Axes
    g.selectAll(".y-axis").remove();
    g.selectAll(".x-axis").remove();

    const yAxis = d3.axisLeft(yScale).ticks(3);
    g.append("g")
      .attr("class", "y-axis")
      .call(yAxis)
      .call((sel) => {
        sel.selectAll("text").attr("fill", "white");
        sel.selectAll("line,path").attr("stroke", "white");
      });

    const xAxis = d3
      .axisBottom<number>(xScale)
      .tickFormat((xIdx) => {
        const b = blocks.find((d) => d.xIndex === xIdx);
        return b ? String(b.blockNumber) : "";
      });
    const xAxisSel = g
      .append("g")
      .attr("class", "x-axis")
      .attr("transform", `translate(0,${innerHeight})`)
      .call(xAxis);

    // Hide overlapping x-axis labels if band too small
    const bw = xScale.bandwidth();
    if (bw < 25) {
      xAxisSel.selectAll<SVGGElement, number>(".tick").each(function (_tickVal, i) {
        if (i /3 % 2 !== 0) d3.select(this).remove();
      });
    }
    xAxisSel.call((sel) => {
      sel.selectAll("text").attr("fill", "white");
      sel.selectAll("line,path").attr("stroke", "white");
    });
  }

  /**
   * resize() - call on window/container resize to recalc width
   */
  function resize() {
    width = element.getBoundingClientRect().width;
    svg.attr("width", width);

    xScale.range([0, getInnerWidth()]);
    redraw();
  }

  /**
   * redraw() - repositions existing boxes/axes without changing data
   */
  function redraw() {
    g.selectAll(".box-group")
      .transition()
      .duration(0)
      .attr("transform", (d: any) => `translate(${xScale(d.xIndex) || 0},0)`);

    g.selectAll(".y-axis").remove();
    g.selectAll(".x-axis").remove();

    const yAxis = d3.axisLeft(yScale).ticks(3);
    g.append("g")
      .attr("class", "y-axis")
      .call(yAxis)
      .call((sel) => {
        sel.selectAll("text").attr("fill", "white");
        sel.selectAll("line,path").attr("stroke", "white");
      });

    const xAxis = d3
      .axisBottom<number>(xScale)
      .tickFormat((xIdx) => {
        const b = blocks.find((d) => d.xIndex === xIdx);
        return b ? String(b.blockNumber) : "";
      });
    const xAxisSel = g
      .append("g")
      .attr("class", "x-axis")
      .attr("transform", `translate(0,${innerHeight})`)
      .call(xAxis);

    const bw = xScale.bandwidth();
    if (bw < 25) {
      xAxisSel.selectAll<SVGGElement, number>(".tick").each(function (_tickVal, i) {
        if (i % 2 !== 0) d3.select(this).remove();
      });
    }
    xAxisSel.call((sel) => {
      sel.selectAll("text").attr("fill", "white");
      sel.selectAll("line,path").attr("stroke", "white");
    });
  }

  return {
    update,
    resize,
  };
}
