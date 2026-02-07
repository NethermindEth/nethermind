// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as THREE from 'three/webgpu';
import {
  Fn, If, instanceIndex, uniform, float, int, time, smoothstep, storage,
} from 'three/tsl';
import { LayoutEdge, PARTICLE_LIFETIME } from './flowTypes';
import { ParticlePool } from './particleSystem';

// A buffer with direct references to the array, attribute, and TSL node
export interface GpuBuffer {
  array: Float32Array;
  attr: THREE.StorageInstancedBufferAttribute;
  node: ReturnType<typeof storage>;
}

function makeBuffer(count: number, type: string): GpuBuffer {
  const itemSize = type === 'vec4' ? 4 : type === 'vec3' ? 3 : type === 'vec2' ? 2 : 1;
  const array = new Float32Array(count * itemSize);
  const attr = new THREE.StorageInstancedBufferAttribute(array, itemSize);
  const node = storage(attr, type, count);
  return { array, attr, node };
}

// Consolidated particle buffers (4 storage buffers instead of 8)
// Compute: positions + state + sizes + edgeData = 4 bindings (limit: 8)
// Render:  positions + sizes + colors = 3 bindings
export function createParticleBuffers(maxParticles: number) {
  const buffers = {
    positions: makeBuffer(maxParticles, 'vec3'),       // xyz world position
    state: makeBuffer(maxParticles, 'vec4'),            // x=progress, y=life, z=edgeIndex, w=seed
    colors: makeBuffer(maxParticles, 'vec3'),           // rgb color
    sizes: makeBuffer(maxParticles, 'vec2'),            // x=currentSize (mutable), y=baseSize (immutable)
  };

  // Initialize edgeIndex (state.z) to -1 (dead) for all particles.
  for (let i = 0; i < maxParticles; i++) {
    buffers.state.array[i * 4 + 2] = -1;
  }
  buffers.state.attr.needsUpdate = true;

  return buffers;
}

// Edge data: 5 vec2 per edge = p0, p1, p2, p3, (sourceHalfH, targetHalfH)
export function createEdgeBuffer(maxEdges: number) {
  return {
    edgeData: makeBuffer(maxEdges * 5, 'vec2'),
    edgeCount: uniform(int(0)),
  };
}

export const cubicBezier = Fn(([t_immutable, p0, p1, p2, p3]: any[]) => {
  const t = float(t_immutable);
  const t1 = float(1.0).sub(t);
  const t2 = t1.mul(t1);
  const t3 = t2.mul(t1);
  const tt = t.mul(t);
  const ttt = tt.mul(t);
  return p0.mul(t3)
    .add(p1.mul(float(3.0).mul(t2).mul(t)))
    .add(p2.mul(float(3.0).mul(t1).mul(tt)))
    .add(p3.mul(ttt));
});

export function createUpdateShader(
  buffers: ReturnType<typeof createParticleBuffers>,
  edgeBuffers: ReturnType<typeof createEdgeBuffer>,
  maxParticles: number
) {
  const dt = uniform(0);

  const computeUpdate = Fn(() => {
    const st = buffers.state.node.element(instanceIndex);
    const ei = st.z; // edgeIndex

    // Only process alive particles (edgeIndex >= 0)
    If(ei.greaterThanEqual(0), () => {
      // Decrease life
      st.y.subAssign(dt);

      // Check if particle should die
      If(st.y.lessThanEqual(0).or(st.x.greaterThan(1.0)), () => {
        st.z.assign(-1);
        buffers.sizes.node.element(instanceIndex).x.assign(0);
      }).Else(() => {
        // Advance progress
        const speed = float(1.0).div(PARTICLE_LIFETIME);
        st.x.addAssign(dt.mul(speed));

        // Look up bezier control points (5 vec2 per edge)
        const baseIdx = ei.toInt().mul(5);
        const p0 = edgeBuffers.edgeData.node.element(baseIdx);
        const p1 = edgeBuffers.edgeData.node.element(baseIdx.add(1));
        const p2 = edgeBuffers.edgeData.node.element(baseIdx.add(2));
        const p3 = edgeBuffers.edgeData.node.element(baseIdx.add(3));
        const heights = edgeBuffers.edgeData.node.element(baseIdx.add(4));

        // Evaluate bezier position
        const progress = st.x;
        const seed = st.w;
        const bezierPos = cubicBezier(progress, p0, p1, p2, p3);

        // Spread particles across source/target node heights
        const lane = seed.mul(2.0).sub(1.0); // -1 to +1 per particle
        const srcSpread = heights.x.mul(lane);
        const tgtSpread = heights.y.mul(lane);
        const yOffset = srcSpread.add(tgtSpread.sub(srcSpread).mul(progress));

        // Add subtle oscillation on top
        const oscillation = time.mul(3.0).add(seed.mul(6.28)).sin().mul(1.5);

        const pos = buffers.positions.node.element(instanceIndex);
        pos.x.assign(bezierPos.x);
        pos.y.assign(bezierPos.y.add(yOffset).add(oscillation));
        pos.z.assign(0);

        // Compute alpha fade: read baseSize (y), write currentSize (x)
        const fadeIn = smoothstep(float(0), float(0.05), progress);
        const fadeOut = smoothstep(float(0), float(0.1), st.y);
        const alpha = fadeIn.mul(fadeOut);
        const sizeEntry = buffers.sizes.node.element(instanceIndex);
        sizeEntry.x.assign(sizeEntry.y.mul(alpha));
      });
    });
  })().compute(maxParticles);

  return { computeUpdate, dt };
}

export function writeSpawnData(
  buffers: ReturnType<typeof createParticleBuffers>,
  pool: ParticlePool,
  spawnedIndices: number[]
): void {
  for (const idx of spawnedIndices) {
    // positions (vec3)
    const pi = idx * 3;
    buffers.positions.array[pi] = pool.initPositions[pi];
    buffers.positions.array[pi + 1] = pool.initPositions[pi + 1];
    buffers.positions.array[pi + 2] = pool.initPositions[pi + 2];

    // state (vec4): progress, life, edgeIndex, seed
    const si = idx * 4;
    buffers.state.array[si] = pool.initProgress[idx];
    buffers.state.array[si + 1] = pool.initLife[idx];
    buffers.state.array[si + 2] = pool.initEdgeIndex[idx];
    buffers.state.array[si + 3] = pool.initSeed[idx];

    // colors (vec3)
    const ci = idx * 3;
    buffers.colors.array[ci] = pool.initColor[ci];
    buffers.colors.array[ci + 1] = pool.initColor[ci + 1];
    buffers.colors.array[ci + 2] = pool.initColor[ci + 2];

    // sizes (vec2): currentSize, baseSize
    const zi = idx * 2;
    buffers.sizes.array[zi] = pool.initSize[idx];
    buffers.sizes.array[zi + 1] = pool.initSize[idx];
  }

  buffers.positions.attr.needsUpdate = true;
  buffers.state.attr.needsUpdate = true;
  buffers.colors.attr.needsUpdate = true;
  buffers.sizes.attr.needsUpdate = true;
}

// Three.js WebGPU backend doesn't re-upload StorageInstancedBufferAttribute
// data after initial creation. We bypass it and write directly to GPU buffers
// using device.queue.writeBuffer(), but ONLY for newly spawned particle indices.
// This avoids overwriting compute shader output (positions, sizes) for existing particles.
export function flushSpawnedToGPU(
  renderer: THREE.WebGPURenderer,
  buffers: ReturnType<typeof createParticleBuffers>,
  spawnedIndices: number[]
): void {
  const backend = (renderer as any).backend;
  if (!backend) return;
  const device = backend.device;
  if (!device) return;

  const bufferDefs: { buf: GpuBuffer; floatsPerItem: number }[] = [
    { buf: buffers.positions, floatsPerItem: 3 },
    { buf: buffers.state, floatsPerItem: 4 },
    { buf: buffers.colors, floatsPerItem: 3 },
    { buf: buffers.sizes, floatsPerItem: 2 },
  ];

  for (const { buf, floatsPerItem } of bufferDefs) {
    const gpuData = backend.get(buf.attr);
    if (!gpuData?.buffer) continue;

    const bytesPerItem = floatsPerItem * 4;
    for (const idx of spawnedIndices) {
      const byteOffset = idx * bytesPerItem;
      device.queue.writeBuffer(
        gpuData.buffer, byteOffset,
        buf.array.buffer, buf.array.byteOffset + byteOffset, bytesPerItem
      );
    }
  }
}

export function flushEdgeBuffersToGPU(
  renderer: THREE.WebGPURenderer,
  edgeBuffers: ReturnType<typeof createEdgeBuffer>
): void {
  const backend = (renderer as any).backend;
  if (!backend) return;
  const device = backend.device;
  if (!device) return;

  const gpuData = backend.get(edgeBuffers.edgeData.attr);
  if (gpuData?.buffer) {
    device.queue.writeBuffer(
      gpuData.buffer, 0,
      edgeBuffers.edgeData.array.buffer,
      edgeBuffers.edgeData.array.byteOffset,
      edgeBuffers.edgeData.array.byteLength
    );
  }
}

export function uploadEdgeData(
  edgeBuffers: ReturnType<typeof createEdgeBuffer>,
  edges: LayoutEdge[]
): void {
  const arr = edgeBuffers.edgeData.array;

  for (let i = 0; i < edges.length; i++) {
    const edge = edges[i];
    const base = i * 5 * 2; // 5 vec2 per edge = 10 floats

    arr[base] = edge.p0.x;
    arr[base + 1] = -edge.p0.y; // Negate Y for Three.js coordinate system

    arr[base + 2] = edge.p1.x;
    arr[base + 3] = -edge.p1.y;

    arr[base + 4] = edge.p2.x;
    arr[base + 5] = -edge.p2.y;

    arr[base + 6] = edge.p3.x;
    arr[base + 7] = -edge.p3.y;

    // Heights: (sourceHalfH, targetHalfH)
    arr[base + 8] = edge.sourceHalfHeight;
    arr[base + 9] = edge.targetHalfHeight;
  }

  edgeBuffers.edgeData.attr.needsUpdate = true;
  edgeBuffers.edgeCount.value = edges.length;
}
