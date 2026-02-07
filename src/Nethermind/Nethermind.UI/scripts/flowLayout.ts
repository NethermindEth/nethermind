// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import { INode, ILink } from './types';
import {
  LayoutNode, LayoutEdge,
  NODE_WIDTH, NODE_PADDING, MARGIN_X, MARGIN_TOP, MARGIN_BOTTOM
} from './flowTypes';

function assignColumns(nodes: INode[], links: ILink[]): Map<string, number> {
  const adjacency = new Map<string, string[]>();
  const inDegree = new Map<string, number>();

  for (const n of nodes) {
    adjacency.set(n.name, []);
    inDegree.set(n.name, 0);
  }

  for (const link of links) {
    adjacency.get(link.source)?.push(link.target);
    inDegree.set(link.target, (inDegree.get(link.target) ?? 0) + 1);
  }

  const columns = new Map<string, number>();
  const queue: string[] = [];

  for (const [name, deg] of inDegree) {
    if (deg === 0) {
      queue.push(name);
      columns.set(name, 0);
    }
  }

  while (queue.length > 0) {
    const current = queue.shift()!;
    const col = columns.get(current)!;
    for (const target of adjacency.get(current) ?? []) {
      const existing = columns.get(target) ?? 0;
      const newCol = Math.max(existing, col + 1);
      columns.set(target, newCol);
      if (!queue.includes(target)) {
        queue.push(target);
      }
    }
  }

  return columns;
}

function computeNodeValues(nodes: INode[], links: ILink[], hashesReceived: number): Map<string, number> {
  const values = new Map<string, number>();

  for (const n of nodes) {
    values.set(n.name, 0);
  }

  for (const link of links) {
    values.set(link.target, (values.get(link.target) ?? 0) + link.value);
  }

  // P2P Network uses hashesReceived as its value
  if (values.has('P2P Network')) {
    values.set('P2P Network', hashesReceived);
  }

  return values;
}

function positionNodesInColumns(
  nodesByColumn: Map<number, LayoutNode[]>,
  maxColumn: number,
  width: number,
  height: number
): void {
  const usableWidth = width - 2 * MARGIN_X;
  const usableHeight = height - MARGIN_TOP - MARGIN_BOTTOM;

  for (const [col, nodes] of nodesByColumn) {
    // Sort: inclusion first (top), then by name — with priority overrides
    const priority: Record<string, number> = { 'Private Order Flow': 0, 'P2P Network': 1 };
    const getPriority = (n: string) => priority[n] ?? 50;
    nodes.sort((a, b) => {
      if (a.inclusion !== b.inclusion) return a.inclusion ? -1 : 1;
      const pa = getPriority(a.name), pb = getPriority(b.name);
      if (pa !== pb) return pa - pb;
      return a.name < b.name ? -1 : 1;
    });

    const colX = maxColumn > 0
      ? MARGIN_X + col * (usableWidth / maxColumn)
      : MARGIN_X;

    // Compute total value in column for proportional heights
    const totalValue = nodes.reduce((sum, n) => sum + n.value, 0);
    const totalPadding = (nodes.length - 1) * NODE_PADDING;
    const availableHeight = Math.max(usableHeight - totalPadding, nodes.length * 4);

    let currentY = MARGIN_TOP;

    for (const node of nodes) {
      node.x = colX;
      node.width = NODE_WIDTH;

      if (totalValue > 0) {
        node.height = Math.max(4, (node.value / totalValue) * availableHeight);
      } else {
        node.height = Math.max(4, availableHeight / nodes.length);
      }

      node.y = currentY + node.height / 2;
      currentY += node.height + NODE_PADDING;
    }
  }
}

function computeEdgePaths(edges: LayoutEdge[], nodes: LayoutNode[]): void {
  for (const edge of edges) {
    const source = nodes[edge.sourceIndex];
    const target = nodes[edge.targetIndex];

    const x0 = source.x + NODE_WIDTH / 2;
    const y0 = source.y;
    const x3 = target.x - NODE_WIDTH / 2;
    const y3 = target.y;

    edge.p0 = { x: x0, y: y0 };
    edge.p1 = { x: x0 + (x3 - x0) * 0.4, y: y0 };
    edge.p2 = { x: x0 + (x3 - x0) * 0.6, y: y3 };
    edge.p3 = { x: x3, y: y3 };
    edge.sourceHalfHeight = source.height / 2;
    edge.targetHalfHeight = target.height / 2;
  }
}

export function computeLayout(
  txPoolNodes: INode[],
  links: ILink[],
  width: number,
  height: number,
  hashesReceived: number
): { nodes: LayoutNode[]; edges: LayoutEdge[] } {
  // Filter zero-value links
  const filteredLinks: ILink[] = [];
  const usedNodes: Record<string, boolean> = {};

  for (const link of links) {
    if (link.value > 0) {
      filteredLinks.push(link);
      usedNodes[link.source] = true;
      usedNodes[link.target] = true;
    }
  }

  const filteredNodes = txPoolNodes.filter(n => usedNodes[n.name]);

  if (filteredNodes.length === 0) {
    return { nodes: [], edges: [] };
  }

  const columns = assignColumns(filteredNodes, filteredLinks);
  const values = computeNodeValues(filteredNodes, filteredLinks, hashesReceived);

  // Build LayoutNode array
  const nodeMap = new Map<string, number>();
  const layoutNodes: LayoutNode[] = filteredNodes.map((n, i) => {
    nodeMap.set(n.name, i);
    return {
      name: n.name,
      inclusion: n.inclusion ?? true,
      x: 0,
      y: 0,
      width: NODE_WIDTH,
      height: 0,
      column: columns.get(n.name) ?? 0,
      value: values.get(n.name) ?? 0,
    };
  });

  // Group by column
  const nodesByColumn = new Map<number, LayoutNode[]>();
  let maxColumn = 0;
  for (const node of layoutNodes) {
    if (node.column > maxColumn) maxColumn = node.column;
    const group = nodesByColumn.get(node.column);
    if (group) group.push(node);
    else nodesByColumn.set(node.column, [node]);
  }

  positionNodesInColumns(nodesByColumn, maxColumn, width, height);

  // Build edges
  const layoutEdges: LayoutEdge[] = [];
  for (const link of filteredLinks) {
    const si = nodeMap.get(link.source);
    const ti = nodeMap.get(link.target);
    if (si === undefined || ti === undefined) continue;

    const targetNode = layoutNodes[ti];
    layoutEdges.push({
      sourceIndex: si,
      targetIndex: ti,
      value: link.value,
      inclusion: targetNode.inclusion,
      p0: { x: 0, y: 0 },
      p1: { x: 0, y: 0 },
      p2: { x: 0, y: 0 },
      p3: { x: 0, y: 0 },
      sourceHalfHeight: 0,
      targetHalfHeight: 0,
    });
  }

  computeEdgePaths(layoutEdges, layoutNodes);

  return { nodes: layoutNodes, edges: layoutEdges };
}
