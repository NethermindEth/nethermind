// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';

export function createRidgelinePlot(
  selection,
  accessor,        // function to extract numeric value from a TransactionReceipt
  maxHistory = 8  // how many ridgelines to keep
) {
  // Set up chart dimensions
  const margin = { top: 20, right: 20, bottom: 30, left: 50 };
  let width = window.innerWidth - (40 + 16);
  const height = 160;

  // Create an <svg> in the given container:
  const svg = selection
    .append('svg')
    .attr('viewBox', [0, 0, width, height])
    .style('overflow', 'visible');

  // Main group
  const g = svg.append('g')
    .attr('transform', `translate(${margin.left},${margin.top})`);

  const innerWidth = width - margin.left - margin.right;
  const innerHeight = height - margin.top - margin.bottom;

  // We'll keep track of ridgeline "distributions" in an array.
  // Each entry = { id: number, bins: d3-histogram array }.
  // We unshift() the newest distribution at index 0,
  // and pop() the oldest if above maxHistory.
  const distributions = [];
  let distributionID = 0; // increment to uniquely key each distribution

  // Scales
  // x-scale for the numeric domain (we recalc domain each update).
  const x = d3.scaleLinear().range([0, innerWidth]);

  // yOffsetScale: band scale for vertical offset of each ridgeline.
  // i=0 (newest) is at the top => y=0, i=maxHistory-1 at bottom => y=innerHeight
  const yOffsetScale = d3.scaleBand()
    .domain(d3.range(maxHistory) as any) // 0..35
    .range([0, innerHeight])
    .padding(0.1);

  // amplitudeScale for how tall each ridgeline curve is.
  const amplitudeScale = d3.scaleLinear()
    .range([0, yOffsetScale.bandwidth()]);

  // Bin generator (you can tweak # of thresholds, etc.).
  const bin = d3.bin()
    .thresholds(10) // # of histogram bins
    .value((d) => d);

  // The area generator for each ridgeline.
  const area = d3.area()
    .curve(d3.curveBasis)
    // Midpoint of the bin on X
    .x((d: any) => x((d.x0 + d.x1) / 2))
    .y0(0) // baseline
    // negative y to go upward in the local coordinate system
    .y1((d) => -amplitudeScale(d.length));

  // We'll store ridgeline groups in a selection:
  let ridgelineGroup = g.selectAll('.ridgeline');

  // Reusable transition
  const t = () => d3.transition().duration(750);

  function update(mergedData) {
    width = window.innerWidth - (40 + 16);
    svg
      .attr('viewBox', [0, 0, width, height])
    // 1) Convert to numeric values via accessor
    const values = mergedData
      .map(accessor)
      .filter((v) => Number.isFinite(v) && v >= 0);

    // 2) Build histogram (array of bins)
    const bins = bin(values);

    // 3) Insert a new distribution at the front
    distributions.unshift({
      id: distributionID++,
      bins
    });

    // 4) If we exceed maxHistory, remove the oldest from the end
    if (distributions.length > maxHistory) {
      distributions.pop();
    }

    // 5) Update the x-domain to fit *all* data in memory
    const allBinEdges = distributions.flatMap((d) =>
      d.bins.map((b) => b.x1)
    );
    const maxX = d3.max(allBinEdges) || 1;
    x.domain([0, maxX]);

    // 6) Update amplitude scale: find global max bin height across all
    const maxCount = d3.max(distributions, (dist) =>
      d3.max(dist.bins, (b: any) => b.length)
    ) || 1;
    amplitudeScale.domain([0, maxCount] as any);

    // 7) DATA BIND: ridgelineGroup <-> distributions
    //    Key by .id so D3 can track enters/exits properly
    ridgelineGroup = ridgelineGroup.data(distributions, (d) => d.id);

    // 8) EXIT
    //    - The bottom-most distribution (which fell off the array) will exit
    ridgelineGroup.exit()
      .transition(t())
      .attr('transform', `translate(0, ${innerHeight + yOffsetScale.bandwidth()})`)
      .remove();

    // 9) ENTER
    const enterGroups = ridgelineGroup.enter()
      .append('g')
      .attr('class', 'ridgeline');

    // The new group is created "above" the chart (y<0) to slide in from top
    enterGroups
      .attr('transform', `translate(0, -${yOffsetScale.bandwidth()})`);

    // Each group has a path for the area
    enterGroups.append('path')
      .attr('fill', 'steelblue')
      .attr('opacity', 0.7)
      .attr('stroke', 'none');

    // 10) MERGE (ENTER + UPDATE)
    ridgelineGroup = enterGroups.merge(ridgelineGroup);

    ridgelineGroup.select('path')
      .transition(t())
      .attr('d', (d) => area(d.bins));

    // 11) For each ridgeline (both new and existing), set the transform
    ridgelineGroup
      .transition(t())
      .attr('transform', (_, i) => `translate(0, ${yOffsetScale(i)})`);

    // 12) (Optional) Re-draw or update an X axis
    g.selectAll('.x-axis').remove();
    g.append('g')
      .attr('class', 'x-axis')
      .attr('transform', `translate(0, ${innerHeight})`)
      .call(d3.axisBottom(x).ticks(5));
  }

  return { update };
}
