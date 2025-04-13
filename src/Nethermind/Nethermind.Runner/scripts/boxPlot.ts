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

  // We'll store up to maxHistory blocks:
  //   { xIndex, blockNumber, stats: {min,q1,median,q3,max,whiskerLow,whiskerHigh}, values[] }
  const blocks: any[] = [];

  // We'll store the median of the oldest block that got removed so the next new block
  // can still compare to it (if it effectively "replaces" that xIndex).
  let lastRemovedMedian: number | undefined;

  // X scale: domain is [0..maxHistory-1]
  const xScale = d3
    .scaleBand<number>()
    .domain(d3.range(maxHistory))
    .range([0, getInnerWidth()])
    .paddingInner(0.3)
    .paddingOuter(0.1);

  // Y scale
  const yScale = d3.scaleLinear().range([innerHeight, 0]);

  function transition() {
    return d3.transition().duration(750);
  }

  // Compute box-plot stats with Tukey whiskers
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
    // 1) Extract & compute stats
    const values = mergedData.map(accessor).filter((v) => Number.isFinite(v) && v >= 0);
    const stats = computeBoxStats(values);

    // 2) If we haven't filled up yet, place new block at xIndex=blocks.length
    if (blocks.length < maxHistory) {
      const xIndex = blocks.length;
      blocks.push({ xIndex, blockNumber: blockNum, stats, values });
    } else {
      // We have max blocks => SHIFT left
      blocks.forEach((b) => {
        b.xIndex -= 1;
      });
      // If the oldest block is now xIndex<0, remove it but remember its median
      while (blocks.length && blocks[0].xIndex < 0) {
        const removed = blocks.shift();
        if (removed) {
          // store its median so the new block can compare
          lastRemovedMedian = removed.stats.median;
        }
      }
      // Insert the new block at xIndex = maxHistory -1 (the right-most slot)
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

    // 4) Data binding
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

    // Box (Q1->Q3)
    enterGroups
      .append("rect")
      .attr("class", "box-rect")
      .attr("stroke", "white")
      .attr("fill", "gray");

    // Median line
    enterGroups.append("line").attr("class", "median-line").attr("stroke", "#ccc").attr("stroke-width", 1);

    // Whisker caps
    enterGroups.append("line").attr("class", "whisker-cap lower").attr("stroke", "#aaa");
    enterGroups.append("line").attr("class", "whisker-cap upper").attr("stroke", "#aaa");

    // Points group
    enterGroups.append("g").attr("class", "points-group");

    // MERGE
    boxGroups = enterGroups.merge(boxGroups);

    // SHIFT/UPDATE TRANSFORM
    boxGroups
      .transition(transition())
      .attr("transform", (d) => `translate(${xScale(d.xIndex) || 0},0) scale(1,1)`);

    // 5) RENDER each box
    boxGroups.each(function (blockData) {
      const sel = d3.select(this);
      const s = blockData.stats;
      const bw = xScale.bandwidth();

      // ================= Color logic (red if median > prev, green if < prev) ===============
      // We'll find the block with xIndex = blockData.xIndex -1
      // If none, we'll use lastRemovedMedian if present and we're at xIndex=0
      let prevMedian: number | undefined;
      const prevBlock = blocks.find((b) => b.xIndex === blockData.xIndex - 1);
      if (prevBlock) {
        prevMedian = prevBlock.stats.median;
      } else if (blockData.xIndex === 0 && lastRemovedMedian !== undefined) {
        // compare with the block that just fell off
        prevMedian = lastRemovedMedian;
      }

      let fillColor = "gray";
      if (prevMedian !== undefined) {
        if (s.median > prevMedian) {
          fillColor = "rgb(127, 63, 63)";
        } else if (s.median < prevMedian) {
          fillColor = "rgb(82, 127, 63)"; // green tinge
        } else {
          fillColor = "rgb(127,127,127)"; // same median => neutral
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

      // Box rect
      sel
        .select<SVGRectElement>(".box-rect")
        .transition(transition())
        .attr("x", 0)
        .attr("width", bw)
        .attr("y", yScale(s.q3))
        .attr("height", Math.max(0, yScale(s.q1) - yScale(s.q3)))
        .attr("fill", fillColor);

      // Median line
      sel
        .select<SVGLineElement>(".median-line")
        .transition(transition())
        .attr("x1", 0)
        .attr("x2", bw)
        .attr("y1", yScale(s.median))
        .attr("y2", yScale(s.median));

      // Whisker caps
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

    // 6) Draw Axes
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
        if (i / 2 % 2 !== 0) d3.select(this).remove();
      });
    }
    xAxisSel.call((sel) => {
      sel.selectAll("text").attr("fill", "white");
      sel.selectAll("line,path").attr("stroke", "white");
    });

    // 7) Draw a median trend line across all blocks
    g.selectAll(".median-trend").remove();

    // We'll create a line from left to right connecting each block's median
    // Sort by xIndex so the line is left -> right
    const sortedBlocks = blocks.filter((b) => b.xIndex >= 0).sort((a, b) => a.xIndex - b.xIndex);

    const lineGen = d3
      .line<any>()
      // use the band center: xScale(d.xIndex) + band/2
      .x((d) => (xScale(d.xIndex) ?? 0) + bw / 2)
      .y((d) => yScale(d.stats.median))
      .curve(d3.curveMonotoneX); // a smooth curve, or use d3.curveLinear for straight lines

    g.append("path")
      .attr("class", "median-trend")
      .datum(sortedBlocks)
      .attr("fill", "none")
      .attr("stroke", "#FFA726")
      .attr("stroke-width", 2)
      .transition(transition())
      .attr("d", lineGen);
  }

  /**
   * resize() - call on window/container resize
   */
  function resize() {
    width = element.getBoundingClientRect().width;
    svg.attr("width", width);

    xScale.range([0, getInnerWidth()]);
    redraw();
  }

  /**
   * redraw() - repositions boxes/axes/trend line without altering data
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

    // Re-draw median trend line
    g.selectAll(".median-trend").remove();

    const sortedBlocks = blocks.filter((b) => b.xIndex >= 0).sort((a, b) => a.xIndex - b.xIndex);
    const bw2 = xScale.bandwidth();
    const lineGen = d3
      .line<any>()
      .x((d) => (xScale(d.xIndex) ?? 0) + bw2 / 2)
      .y((d) => yScale(d.stats.median))
      .curve(d3.curveMonotoneX);

    g.append("path")
      .attr("class", "median-trend")
      .datum(sortedBlocks)
      .attr("fill", "none")
      .attr("stroke", "#FFA726")
      .attr("stroke-width", 2)
      .attr("d", lineGen);
  }

  return {
    update,
    resize,
  };
}
