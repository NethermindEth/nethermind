// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

export interface LayoutNode {
  name: string;
  inclusion: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
  column: number;
  value: number;
}

export interface LayoutEdge {
  sourceIndex: number;
  targetIndex: number;
  value: number;
  inclusion: boolean;
  p0: { x: number; y: number };
  p1: { x: number; y: number };
  p2: { x: number; y: number };
  p3: { x: number; y: number };
  sourceHalfHeight: number;
  targetHalfHeight: number;
}

export const MAX_PARTICLES = 16384;
export const MAX_EDGES = 64;
export const PARTICLE_LIFETIME = 2.0;
export const MIN_EMIT_RATE = 0.5;
export const NODE_WIDTH = 10;
export const NODE_PADDING = 30;
export const MARGIN_X = 100;
export const MARGIN_TOP = 20;
export const MARGIN_BOTTOM = 25;
