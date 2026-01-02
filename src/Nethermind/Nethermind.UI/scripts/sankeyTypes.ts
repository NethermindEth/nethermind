// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


// Our custom node interface extends the generic SankeyNode:
export interface SankeyNode {
  name: string;
  value: number;
  targetLinks: SankeyLink[];
  inclusion?: boolean;

  // After .sankey(...) runs, d3 sets the layout properties:
  x0: number;
  x1: number;
  y0: number;
  y1: number;

  // And an index (node.index) that can appear after the layout:
  index?: number;
}

// Our custom link interface extends the generic SankeyLink:
export interface SankeyLink {
  value: number;

  // Once .sankey(...) has run, these become full references:
  source: SankeyNode;
  target: SankeyNode;

  // Also set by Sankey:
  width?: number;
  index?: number;
}
