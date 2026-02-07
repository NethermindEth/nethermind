// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import { LayoutEdge, PARTICLE_LIFETIME, MIN_EMIT_RATE } from './flowTypes';

class Emitter {
  edgeIndex: number;
  rate: number;
  accumulator: number;
  inclusion: boolean;
  jitterRange: number;
  pendingJitter: number;

  constructor(edgeIndex: number, rate: number, inclusion: boolean) {
    this.edgeIndex = edgeIndex;
    this.rate = rate;
    this.accumulator = 0;
    this.inclusion = inclusion;
    this.jitterRange = rate > 0 ? 1.0 / rate : 1.0;
    this.pendingJitter = Math.random() * this.jitterRange;
  }

  tick(dt: number): number {
    this.accumulator += this.rate * dt;
    let count = 0;

    while (this.accumulator >= 1.0 + this.pendingJitter) {
      this.accumulator -= 1.0;
      this.pendingJitter = Math.random() * this.jitterRange;
      count++;
    }

    return count;
  }
}

export class ParticlePool {
  private maxParticles: number;
  private freeList: number[];
  private emitters: Map<number, Emitter>;
  private previousValues: Map<number, number>;
  private hasBaseline = false;
  private lastUpdateTime = 0;
  private spawnTimes: Float64Array;
  private spawnLifetimes: Float32Array;

  initPositions: Float32Array;
  initProgress: Float32Array;
  initEdgeIndex: Float32Array;
  initLife: Float32Array;
  initColor: Float32Array;
  initSize: Float32Array;
  initSeed: Float32Array;

  constructor(maxParticles: number) {
    this.maxParticles = maxParticles;
    this.freeList = [];
    this.emitters = new Map();
    this.previousValues = new Map();
    this.spawnTimes = new Float64Array(maxParticles);
    this.spawnLifetimes = new Float32Array(maxParticles);

    this.initPositions = new Float32Array(maxParticles * 3);
    this.initProgress = new Float32Array(maxParticles);
    this.initEdgeIndex = new Float32Array(maxParticles);
    this.initLife = new Float32Array(maxParticles);
    this.initColor = new Float32Array(maxParticles * 3);
    this.initSize = new Float32Array(maxParticles);
    this.initSeed = new Float32Array(maxParticles);

    // Initialize free list and edge indices
    for (let i = maxParticles - 1; i >= 0; i--) {
      this.freeList.push(i);
      this.initEdgeIndex[i] = -1;
    }
  }

  updateEmitters(edges: LayoutEdge[]): void {
    this.lastUpdateTime = performance.now();
    const activeEdges = new Set<number>();

    if (!this.hasBaseline) {
      // First SSE update: store baseline values, start at MIN rate.
      // Without this, delta = full cumulative value → all edges at MAX_EMIT_RATE.
      this.hasBaseline = true;
      for (let i = 0; i < edges.length; i++) {
        activeEdges.add(i);
        this.previousValues.set(i, edges[i].value);
        this.emitters.set(i, new Emitter(i, MIN_EMIT_RATE, edges[i].inclusion));
      }
      return;
    }

    for (let i = 0; i < edges.length; i++) {
      activeEdges.add(i);
      const edge = edges[i];
      const prev = this.previousValues.get(i) ?? edge.value;
      const delta = Math.max(0, edge.value - prev);

      // 1 particle per transaction — rate = delta tx/sec (SSE arrives ~1s)
      const targetRate = Math.max(MIN_EMIT_RATE, delta);

      const existing = this.emitters.get(i);
      if (existing) {
        existing.rate = existing.rate + (targetRate - existing.rate) * 0.3;
        existing.inclusion = edge.inclusion;
        existing.jitterRange = existing.rate > 0 ? 1.0 / existing.rate : 1.0;
      } else {
        this.emitters.set(i, new Emitter(i, targetRate, edge.inclusion));
      }

      this.previousValues.set(i, edge.value);
    }

    // Deactivate removed edges
    for (const [idx, emitter] of this.emitters) {
      if (!activeEdges.has(idx)) {
        emitter.rate = 0;
      }
    }
  }

  spawn(dt: number, edges: LayoutEdge[]): number[] {
    const spawned: number[] = [];

    // Decay emission when no SSE data arrives (server stopped / tab switch)
    const timeSinceUpdate = (performance.now() - this.lastUpdateTime) / 1000;
    if (timeSinceUpdate > 2.0) {
      // Linearly decay to 0 over 2 seconds after the 2s grace period
      const decay = Math.max(0, 1.0 - (timeSinceUpdate - 2.0) / 2.0);
      if (decay <= 0) return spawned;
      dt *= decay;
    }

    for (const [, emitter] of this.emitters) {
      if (emitter.rate <= 0) continue;

      const count = emitter.tick(dt);
      const edge = edges[emitter.edgeIndex];
      if (!edge) continue;

      for (let i = 0; i < count; i++) {
        if (this.freeList.length === 0) return spawned;

        const idx = this.freeList.pop()!;
        const now = performance.now();

        // Position at source (negate Y for Three.js coordinate system)
        const pi = idx * 3;
        this.initPositions[pi] = edge.p0.x;
        this.initPositions[pi + 1] = -edge.p0.y;
        this.initPositions[pi + 2] = 0;

        this.initProgress[idx] = 0;
        this.initEdgeIndex[idx] = emitter.edgeIndex;

        const life = PARTICLE_LIFETIME;
        this.initLife[idx] = life;

        // Neon color with brightness variation
        const ci = idx * 3;
        const brightness = 0.85 + Math.random() * 0.3;
        if (emitter.inclusion) {
          // Electric cyan
          this.initColor[ci] = 0.0;
          this.initColor[ci + 1] = 0.9 * brightness;
          this.initColor[ci + 2] = 1.0 * brightness;
        } else {
          // Hot orange
          this.initColor[ci] = 1.0 * brightness;
          this.initColor[ci + 1] = 0.4 * brightness;
          this.initColor[ci + 2] = 0.0;
        }

        this.initSize[idx] = 4 + Math.random() * 5.0;
        this.initSeed[idx] = Math.random();

        this.spawnTimes[idx] = now;
        this.spawnLifetimes[idx] = life;

        spawned.push(idx);
      }
    }

    return spawned;
  }

  recycleDead(now: number): void {
    for (let i = 0; i < this.maxParticles; i++) {
      if (this.spawnTimes[i] === 0) continue;
      const elapsed = (now - this.spawnTimes[i]) / 1000;
      if (elapsed > this.spawnLifetimes[i] + 0.1) {
        this.freeList.push(i);
        this.spawnTimes[i] = 0;
        this.initEdgeIndex[i] = -1;
      }
    }
  }
}
