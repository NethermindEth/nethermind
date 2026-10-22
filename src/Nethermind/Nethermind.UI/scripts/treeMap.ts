// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';

/**
 * Builds or updates a treemap in a given <svg> element.
 *
 * @param element   The <svg> HTMLElement container for the treemap
 * @param size      An object { width, height } for the overall treemap dimensions
 * @param totalSize The capacity or "max size" to compare against the sum of sizes (e.g. gasLimit)
 * @param data      Flat array of items, each representing a leaf node
 * @param keyFn     Maps each item -> unique string (transaction hash, e.g.)
 * @param groupFn   Maps each item -> group name (for hierarchical grouping)
 * @param sizeFn    Maps each item -> numeric size (for area)
 * @param colorFn   Maps each item -> numeric value (for color scale)
 */
export function updateTreemap<T>(
  element: HTMLElement,
  height: number,
  totalSize: number,
  data: T[],
  keyFn: (d: T) => string,
  orderFn: (d: T) => number,
  sizeFn: (d: T) => number,
  colorFn: (d: T) => number
) {
  const width = window.innerWidth - (40 + 16);
  const children = [...data.map((d) => ({
    name: keyFn(d),  // leaf node name
    item: d,         // store original data
    size: sizeFn(d)  // numeric value for area
  }))];
  const rootData = { name: "root", children };

  // 3) Build a D3 hierarchy and sum by "size"
  const root = d3.hierarchy(rootData)
    .sum((node: any) => node.children ? 0 : sizeFn(node.data ? node.data.item : node.item ))
    .sort((node: any) => node.children ? 0 : orderFn(node.data ? node.data.item : node.item ));

  // 4) Figure out how much "used" size we have relative to the totalSize
  const used = root.value ?? 0;
  // The fraction of capacity used
  const ratio = used > 0 ? used / totalSize : 0;
  // We'll clamp so that if used > totalSize, the treemap still fits.
  const usedWidth = Math.min(width, width * ratio);

  // 5) Apply a treemap layout to the hierarchy,
  //    giving it [usedWidth, height] so it will only fill proportionally.
  d3.treemap()
    .size([usedWidth, height - 1])
    .round(true)
    .tile(d3.treemapSquarify.ratio(1))
    .paddingOuter(0.5)
    .paddingInner(2)(root);

  // 7) Define a numeric scale for color strength from colorFn
  //    (e.g. from min effectiveGasPrice to max)
  //
  // 1) Compute min/max from your data
  const [minVal, maxVal] = d3.extent(data, colorFn);

  // 2) For a log scale, make sure your minimum is > 0. 
  //    If minVal <= 0, you need to clamp or shift up to a safe positive value.
  //    For example:
  const safeMin = minVal && minVal > 0 ? minVal : 1e-6; // pick an epsilon for 0 or negative
  const safeMax = maxVal || 1; // fallback if maxVal is null/undefined

  // 4) Build a scaleSequentialLog using that interpolator
  //    This will map [safeMin ... safeMax] => [0..1], 
  //    then feed that t ∈ [0..1] into your custom colorInterpolator.
  const colorScale = d3
    .scaleSequentialLog(d3.interpolateCool)
    .domain([safeMin, safeMax]);

  // 5) Finally, use it in your getColor(...) helper:
  function getColor(item: T): string {
    const value = colorFn(item);
    // If value <= 0, clamp or shift it so we don't break the log scale:
    const safeValue = value > 0 ? value : 1e-6;
    return colorScale(safeValue);
  }

  // 7) Get a D3 selection for the <svg> element
  let svg = d3.select(element).select('svg');
  if (svg.empty()) {
    svg = d3.select(element)
      .append('svg')
      .attr('width', width)
      .attr('height', height)
      .attr('viewBox', [0, 0, width, height]) as any;

    const defs = svg.append("defs");

    const pattern = defs
      .append("pattern")
      .attr("id", "unusedStripes")
      .attr("patternUnits", "userSpaceOnUse")
      .attr("width", 8)
      .attr("height", 8)

    pattern
      .append("rect")
      .attr("class", "pattern-bg")
      .attr("width", 8)
      .attr("height", 8)
      .attr("fill", "#444");

    pattern
      .append("path")
          .attr("d", "M0,0 l8,8")
          .attr("stroke", "#000")
          .attr("stroke-width", 1);

    svg.append('rect')
      .attr("class", "unused")
      // fill with our diagonal stripes pattern
      .attr("fill", "url(#unusedStripes)")
      .attr("opacity", 1)
      .attr("width", width)
      .attr("height", height)
      .attr("stroke", "#fff")
      .attr("stroke-width", 1);

      // Arrow at the start
      defs
        .append("marker")
        .attr("id", "arrowStart")
        .attr("markerWidth", 10)
        .attr("markerHeight", 10)
        .attr("refX", 5)     // x reference point where arrow is "anchored"
        .attr("refY", 5)     // y reference point (vertical center)
        .attr("orient", "auto") // orient automatically along the line
        .attr("markerUnits", "strokeWidth") // scale arrow based on line width
        .append("path")
        .attr("d", "M10,0 L0,5 L10,10") // a simple arrow shape
        .attr("fill", "#ccc");

      // Arrow at the end
      defs
        .append("marker")
        .attr("id", "arrowEnd")
        .attr("markerWidth", 10)
        .attr("markerHeight", 10)
        .attr("refX", 5)
        .attr("refY", 5)
        .attr("orient", "auto")
        .attr("markerUnits", "strokeWidth")
        .append("path")
        .attr("d", "M0,0 L10,5 L0,10") // mirrored arrow shape
        .attr("fill", "#ccc");
  }

  svg
    .attr('width', width)
    .attr('height', height)
    .attr('viewBox', [0, 0, width, height]);

  svg.selectAll('rect.unused')
    .attr("width", width);

  // 8) We'll render leaf nodes only. Each leaf has { name, item, size } in d.data.
  const leaves = root.leaves();

  const node = svg
    .selectAll<SVGGElement, d3.HierarchyNode<any>>("g.node")
    .data(leaves, (d) => d.data.name); // key by the leaf's name

  // 11) Use .join(...) with transitions
  node.join(
    // ENTER
    (enter) => {
      const gEnter = enter
        .append("g")
        .attr("class", "node")
        .attr("data-hash", (d: any) => d.data.name)
        // Start each group at final XY but with zero size + opacity=0
        .attr("transform", (d: any) => `translate(${d.x0},${d.y0})`)
        .attr("opacity", 0);

      gEnter
        .append("rect")
        .attr("stroke", "#000")
        .attr("stroke-width", 0.5)
        .attr("width", 0)
        .attr("height", 0)
        .attr("fill", (d: any) => getColor(d.data.item));

      // Transition in
      gEnter
        .transition()
        .duration(600)
        .attr("opacity", 1);

      gEnter
        .select("rect")
        .transition()
        .duration(600)
        .attr("width", (d: any) => d.x1 - d.x0)
        .attr("height", (d: any) => d.y1 - d.y0);

      return gEnter;
    },
    // UPDATE
    (update) => {
      update
        .transition()
        .duration(600)
        .attr("transform", (d: any) => `translate(${d.x0},${d.y0})`)
        .attr("opacity", 1);

      update
        .select("rect")
        .transition()
        .duration(600)
        .attr("width", (d: any) => d.x1 - d.x0)
        .attr("height", (d: any) => d.y1 - d.y0)
        .attr("fill", (d: any) => getColor(d.data.item));

      return update;
    },
    // EXIT
    (exit) => {
      exit
        .transition()
        .duration(600)
        .attr("opacity", 0)
        .remove();
    }
  );


  //
  // 10) Draw a border or label for the "unused" area if used < totalSize
  const leftoverRatio = (totalSize - used) / totalSize;
  const showLeftoverLabel = leftoverRatio >= 0.1; // i.e. >= 10%

  // If leftoverRatio >= 0.1, we show a label with line
  // The object below can carry leftover ratio & geometry (x, w).
  const leftoverData = showLeftoverLabel
    ? [
      {
        key: "unused-label",
        x: usedWidth,       // the X where "unused" area starts
        w: width - usedWidth, // how wide the leftover region is
        ratio: leftoverRatio
      },
    ]
    : [];

  svg
    .selectAll<SVGLineElement, { key: string; x: number; w: number; ratio: number }>("line.unused-arrow")
    .data(leftoverData, (d) => d.key)
    .join(
      (enter) =>
        enter
          .append("line")
          .attr("class", "unused-arrow")
          // Position the line horizontally from x1..x2
          .attr("x1", (d) => d.x + 10)               // slight offset from the left edge
          .attr("x2", (d) => d.x + d.w - 10)         // slight offset from the right edge
          .attr("y1", 145)                            // pick your desired vertical offset
          .attr("y2", 145)
          .attr("stroke", "#ccc")
          .attr("stroke-width", 2)
          .attr("marker-start", "url(#arrowStart)")
          .attr("marker-end", "url(#arrowEnd)")
          .attr("opacity", 0)
          .call((enterSel) =>
            enterSel
              .transition()
              .duration(600)
              .attr("opacity", 1)
          ),
      (update) =>
        update
          .transition()
          .duration(600)
          .attr("x1", (d) => d.x + 10)
          .attr("x2", (d) => d.x + d.w - 10),
      (exit) =>
        exit
          .transition()
          .duration(600)
          .attr("opacity", 0)
          .remove()
    );

  svg
    .selectAll<SVGTextElement, { key: string; x: number; w: number; ratio: number }>("text.unused-label")
    .data(leftoverData, (d) => d.key)
    .join(
      (enter) =>
        enter
          .append("text")
          .attr("class", "unused-label")
          .attr("x", (d) => d.x + d.w / 2)
          .attr("y", 135) // slightly above the line (which is at y=40)
          .attr("fill", "#ccc")
          .attr("font-size", 14)
          .attr("text-anchor", "middle")
          .attr("opacity", 0)
          .text((d) => `${(d.ratio * 100).toFixed(1)}% available`)
          .call((enterSel) =>
            enterSel
              .transition()
              .duration(600)
              .attr("opacity", 1)
          ),
      (update) =>
        update
          .transition()
          .duration(600)
          .attr("x", (d) => d.x + d.w / 2)
          .text((d) => `${(d.ratio * 100).toFixed(1)}% available`),
      (exit) =>
        exit
          .transition()
          .duration(600)
          .attr("opacity", 0)
          .remove()
    );
}
