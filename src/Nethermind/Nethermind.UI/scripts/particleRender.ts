// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as THREE from 'three/webgpu';
import { instanceIndex, vertexIndex, uv, vec2, vec3, float, mix } from 'three/tsl';
import { LayoutNode, LayoutEdge } from './flowTypes';
import { createParticleBuffers, createEdgeBuffer, cubicBezier } from './particleCompute';

export function createParticleMesh(
  maxParticles: number,
  buffers: ReturnType<typeof createParticleBuffers>
): THREE.InstancedMesh {
  const geometry = new THREE.PlaneGeometry(1, 1);

  const material = new THREE.SpriteNodeMaterial();
  material.positionNode = buffers.positions.node.element(instanceIndex);
  material.scaleNode = buffers.sizes.node.element(instanceIndex).x;
  material.colorNode = buffers.colors.node.element(instanceIndex);

  // Soft circular glow — radial falloff from center
  const dist = uv().sub(vec2(0.5, 0.5)).length().mul(2.0);
  material.opacityNode = float(1.0).sub(dist).max(0.0).pow(1.5);

  material.transparent = true;
  material.blending = THREE.AdditiveBlending;
  material.depthWrite = false;

  const mesh = new THREE.InstancedMesh(geometry, material, maxParticles);
  mesh.frustumCulled = false;
  return mesh;
}

export function createNodeMeshes(
  nodes: LayoutNode[],
  scene: THREE.Scene,
  existing: Map<string, THREE.Mesh>
): Map<string, THREE.Mesh> {
  const result = new Map<string, THREE.Mesh>();
  const seenNames = new Set<string>();

  for (const node of nodes) {
    seenNames.add(node.name);

    let mesh = existing.get(node.name);
    if (!mesh) {
      const geometry = new THREE.PlaneGeometry(1, 1);
      const material = new THREE.MeshBasicNodeMaterial();
      material.side = THREE.DoubleSide;
      material.transparent = true;
      material.opacity = 0.35;
      mesh = new THREE.Mesh(geometry, material);
      scene.add(mesh);
    }

    mesh.position.set(node.x, -node.y, -0.1);
    mesh.scale.set(node.width, node.height, 1);

    const mat = mesh.material as THREE.MeshBasicNodeMaterial;
    if (node.name === 'Tx Pool' || node.name === 'Added To Block') {
      mat.color = new THREE.Color(0xFFB300); // amber gold
    } else if (node.inclusion) {
      mat.color = new THREE.Color(0x00E5FF); // electric cyan
    } else {
      mat.color = new THREE.Color(0x666666);
    }

    result.set(node.name, mesh);
  }

  // Remove nodes that no longer exist
  for (const [name, mesh] of existing) {
    if (!seenNames.has(name)) {
      scene.remove(mesh);
      mesh.geometry.dispose();
      (mesh.material as THREE.Material).dispose();
    }
  }

  return result;
}

export function createEdgeCurves(
  edges: LayoutEdge[],
  scene: THREE.Scene,
  existing: THREE.Line[]
): THREE.Line[] {
  for (const line of existing) {
    scene.remove(line);
    line.geometry.dispose();
    (line.material as THREE.Material).dispose();
  }

  const result: THREE.Line[] = [];

  for (const edge of edges) {
    const curve = new THREE.CubicBezierCurve3(
      new THREE.Vector3(edge.p0.x, -edge.p0.y, -0.2),
      new THREE.Vector3(edge.p1.x, -edge.p1.y, -0.2),
      new THREE.Vector3(edge.p2.x, -edge.p2.y, -0.2),
      new THREE.Vector3(edge.p3.x, -edge.p3.y, -0.2)
    );

    const points = curve.getPoints(48);
    const geometry = new THREE.BufferGeometry().setFromPoints(points);
    const material = new THREE.LineBasicMaterial({
      color: edge.inclusion ? 0x004D66 : 0x332200,
      transparent: true,
      opacity: 0.25,
    });

    const line = new THREE.Line(geometry, material);
    scene.add(line);
    result.push(line);
  }

  return result;
}

const TRAIL_SAMPLES = 16;

export function createTrailLines(
  maxParticles: number,
  buffers: ReturnType<typeof createParticleBuffers>,
  edgeBuffers: ReturnType<typeof createEdgeBuffer>
): THREE.LineSegments {
  const totalVerts = maxParticles * TRAIL_SAMPLES;
  const positions = new Float32Array(totalVerts * 3);
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));

  // Index buffer: connect adjacent samples as line segment pairs
  const indices = new Uint32Array(maxParticles * (TRAIL_SAMPLES - 1) * 2);
  let idx = 0;
  for (let p = 0; p < maxParticles; p++) {
    const base = p * TRAIL_SAMPLES;
    for (let s = 0; s < TRAIL_SAMPLES - 1; s++) {
      indices[idx++] = base + s;
      indices[idx++] = base + s + 1;
    }
  }
  geometry.setIndex(new THREE.BufferAttribute(indices, 1));

  const material = new THREE.LineBasicNodeMaterial();

  // Determine which particle and which sample point this vertex is
  const vid = float(vertexIndex);
  const particleIdx = vid.div(float(TRAIL_SAMPLES)).floor().toInt();
  const sampleIdx = vid.mod(float(TRAIL_SAMPLES));
  const trailFrac = sampleIdx.div(float(TRAIL_SAMPLES - 1)); // 0→1 along trail

  // Read particle state
  const st = buffers.state.node.element(particleIdx);
  const edgeIdx = st.z;
  const progress = st.x;
  const life = st.y;
  const seed = st.w;

  // Compute bezier t: full curve from source (0) to destination (1)
  const t = trailFrac;

  // Read edge bezier control points (5 vec2 per edge)
  const safeIdx = edgeIdx.max(float(0)).toInt();
  const baseIdx = safeIdx.mul(5);
  const p0 = edgeBuffers.edgeData.node.element(baseIdx);
  const p1 = edgeBuffers.edgeData.node.element(baseIdx.add(1));
  const p2 = edgeBuffers.edgeData.node.element(baseIdx.add(2));
  const p3 = edgeBuffers.edgeData.node.element(baseIdx.add(3));
  const heights = edgeBuffers.edgeData.node.element(baseIdx.add(4));

  // Evaluate bezier at t
  const bezierPos = cubicBezier(t, p0, p1, p2, p3);

  // Height spread (same as compute shader)
  const lane = seed.mul(2.0).sub(1.0);
  const srcSpread = heights.x.mul(lane);
  const tgtSpread = heights.y.mul(lane);
  const yOffset = srcSpread.add(tgtSpread.sub(srcSpread).mul(t));

  material.positionNode = vec3(bezierPos.x, bezierPos.y.add(yOffset), float(0));

  // Color from particle
  const particleColor = buffers.colors.node.element(particleIdx);
  material.colorNode = particleColor;

  // Solid line while particle is alive, smooth fade in/out at birth/death
  const alive = edgeIdx.add(1.0).clamp(0.0, 1.0);
  const fadeIn = progress.mul(10.0).clamp(0.0, 1.0);
  const fadeOut = life.mul(5.0).clamp(0.0, 1.0);
  material.opacityNode = alive.mul(fadeIn).mul(fadeOut).mul(float(0.2));

  material.transparent = true;
  material.depthWrite = false;

  const lines = new THREE.LineSegments(geometry, material);
  lines.frustumCulled = false;
  return lines;
}
