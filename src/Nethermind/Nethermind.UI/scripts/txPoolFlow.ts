// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as THREE from 'three/webgpu';
import { TxPool, INode } from './types';
import { LayoutNode, LayoutEdge, MAX_PARTICLES, MAX_EDGES } from './flowTypes';
import { computeLayout } from './flowLayout';
import { ParticlePool } from './particleSystem';
import {
  createParticleBuffers, createEdgeBuffer,
  createUpdateShader, writeSpawnData, uploadEdgeData,
  flushSpawnedToGPU, flushEdgeBuffersToGPU
} from './particleCompute';
import { createParticleMesh, createNodeMeshes, createEdgeCurves, createTrailLines } from './particleRender';

export class TxPoolFlow {
  private container: HTMLElement;
  private canvas: HTMLCanvasElement | null = null;
  private renderer: THREE.WebGPURenderer | null = null;
  private scene: THREE.Scene | null = null;
  private camera: THREE.OrthographicCamera | null = null;
  private width: number;
  private height = 250;

  private particlePool: ParticlePool | null = null;
  private particleBuffers: ReturnType<typeof createParticleBuffers> | null = null;
  private edgeBuffers: ReturnType<typeof createEdgeBuffer> | null = null;
  private computeUpdate: any = null;
  private dtUniform: any = null;
  private particleMesh: THREE.InstancedMesh | null = null;
  private trailLines: THREE.LineSegments | null = null;
  private nodeMeshes = new Map<string, THREE.Mesh>();
  private edgeCurves: THREE.Line[] = [];
  private labelContainer: HTMLElement | null = null;

  private currentNodes: LayoutNode[] = [];
  private currentEdges: LayoutEdge[] = [];

  private animFrameId = 0;
  private lastFrameTime = 0;
  private isActive = false;
  private isInitialized = false;
  private isInitializing = false;
  private fallbackMode = false;
  private pendingUpdate: { txPoolNodes: INode[]; data: TxPool } | null = null;

  private visibilityHandler: (() => void) | null = null;

  constructor(container: string) {
    this.container = document.querySelector(container) as HTMLElement;
    this.width = window.innerWidth - (40 + 16);
  }

  private async initialize(): Promise<void> {
    this.isInitializing = true;

    try {
      // Create canvas (in normal flow so it gives the container height, like the old SVG)
      this.canvas = document.createElement('canvas');
      this.canvas.style.display = 'block';
      this.canvas.style.pointerEvents = 'none';
      this.container.insertBefore(this.canvas, this.container.firstChild);

      // Create renderer
      this.renderer = new THREE.WebGPURenderer({
        canvas: this.canvas,
        antialias: true,
        alpha: true,
      });
      this.renderer.setSize(this.width, this.height);
      this.renderer.setPixelRatio(window.devicePixelRatio);
      this.renderer.setClearColor(0x000000, 0);

      await this.renderer.init();

      // Scene
      this.scene = new THREE.Scene();

      // Orthographic camera: Y-down coordinate system
      this.camera = new THREE.OrthographicCamera(
        0, this.width, 0, -this.height, -1, 10
      );

      // Particle system
      this.particleBuffers = createParticleBuffers(MAX_PARTICLES);
      this.edgeBuffers = createEdgeBuffer(MAX_EDGES);

      const { computeUpdate, dt } = createUpdateShader(
        this.particleBuffers, this.edgeBuffers, MAX_PARTICLES
      );
      this.computeUpdate = computeUpdate;
      this.dtUniform = dt;

      // Trail lines (added first so they render behind particles)
      this.trailLines = createTrailLines(MAX_PARTICLES, this.particleBuffers, this.edgeBuffers);
      this.scene.add(this.trailLines);

      // Particle mesh
      this.particleMesh = createParticleMesh(MAX_PARTICLES, this.particleBuffers);
      this.scene.add(this.particleMesh);

      // Label overlay
      this.labelContainer = document.createElement('div');
      this.labelContainer.className = 'node-labels';
      this.container.appendChild(this.labelContainer);

      // Particle pool
      this.particlePool = new ParticlePool(MAX_PARTICLES);

      // Visibility change handler
      this.visibilityHandler = () => {
        if (document.hidden) {
          this.stop();
        } else {
          this.lastFrameTime = performance.now();
          if (this.currentEdges.length > 0) this.start();
        }
      };
      document.addEventListener('visibilitychange', this.visibilityHandler);

      this.isInitialized = true;
      this.isInitializing = false;

      // Process buffered update
      if (this.pendingUpdate) {
        const { txPoolNodes, data } = this.pendingUpdate;
        this.pendingUpdate = null;
        this.updateInternal(txPoolNodes, data);
      }
    } catch (e) {
      console.warn('WebGPU initialization failed, falling back:', e);
      this.fallbackMode = true;
      this.isInitializing = false;
    }
  }

  public update(txPoolNodes: INode[], data: TxPool): void {
    if (this.fallbackMode) return;

    if (!this.isInitialized && !this.isInitializing) {
      this.pendingUpdate = { txPoolNodes, data };
      this.initialize();
      return;
    }

    if (this.isInitializing) {
      this.pendingUpdate = { txPoolNodes, data };
      return;
    }

    this.updateInternal(txPoolNodes, data);
  }

  private updateInternal(txPoolNodes: INode[], data: TxPool): void {
    const { nodes, edges } = computeLayout(
      txPoolNodes, data.links, this.width, this.height, data.hashesReceived
    );

    this.currentNodes = nodes;
    this.currentEdges = edges;

    this.particlePool!.updateEmitters(edges);
    uploadEdgeData(this.edgeBuffers!, edges);
    flushEdgeBuffersToGPU(this.renderer!, this.edgeBuffers!);

    this.nodeMeshes = createNodeMeshes(nodes, this.scene!, this.nodeMeshes);
    this.edgeCurves = createEdgeCurves(edges, this.scene!, this.edgeCurves);
    this.updateLabels(nodes);

    if (!this.isActive) this.start();
  }

  private updateLabels(nodes: LayoutNode[]): void {
    if (!this.labelContainer) return;
    this.labelContainer.innerHTML = '';

    for (const node of nodes) {
      const label = document.createElement('div');
      label.className = 'node-label';

      const isRight = !node.inclusion;
      label.style.position = 'absolute';
      label.style.top = `${node.y - 10}px`;

      if (isRight) {
        label.style.left = `${node.x + node.width / 2 + 6}px`;
        label.style.textAlign = 'left';
      } else {
        label.style.right = `${this.width - node.x + node.width / 2 + 6}px`;
        label.style.textAlign = 'right';
      }

      const nameSpan = document.createElement('span');
      nameSpan.textContent = node.name;

      const valueSpan = document.createElement('span');
      valueSpan.className = 'node-value';
      valueSpan.textContent = node.value.toLocaleString();

      label.appendChild(nameSpan);
      label.appendChild(valueSpan);
      this.labelContainer.appendChild(label);
    }
  }

  private start(): void {
    if (this.isActive) return;
    this.isActive = true;
    this.lastFrameTime = performance.now();
    this.animFrameId = requestAnimationFrame(this.tick);
  }

  private stop(): void {
    this.isActive = false;
    if (this.animFrameId) {
      cancelAnimationFrame(this.animFrameId);
      this.animFrameId = 0;
    }
  }

  private tick = (): void => {
    if (!this.isActive) return;
    this.animFrameId = requestAnimationFrame(this.tick);

    if (document.hidden) return;

    const now = performance.now();
    const dt = Math.min((now - this.lastFrameTime) / 1000, 0.05);
    this.lastFrameTime = now;

    // Recycle dead particles
    this.particlePool!.recycleDead(now);

    // Spawn new particles
    const spawned = this.particlePool!.spawn(dt, this.currentEdges);
    if (spawned.length > 0) {
      writeSpawnData(this.particleBuffers!, this.particlePool!, spawned);
      // Bypass Three.js needsUpdate (broken for storage buffers after init)
      // and write directly to GPU buffers via WebGPU device.queue.writeBuffer
      // Only flush spawned indices to avoid overwriting compute shader output
      flushSpawnedToGPU(this.renderer!, this.particleBuffers!, spawned);
    }

    // GPU particle simulation
    this.dtUniform.value = dt;
    this.renderer!.compute(this.computeUpdate);

    // Render
    this.renderer!.renderAsync(this.scene!, this.camera!);
  };

  public resize(): void {
    this.width = window.innerWidth - (40 + 16);
    if (!this.isInitialized || !this.renderer || !this.camera) return;

    this.renderer.setSize(this.width, this.height);
    this.camera.right = this.width;
    this.camera.bottom = -this.height;
    this.camera.updateProjectionMatrix();
  }

  public setActive(active: boolean): void {
    if (active) {
      if (this.isInitialized && this.currentEdges.length > 0) this.start();
    } else {
      this.stop();
    }
  }

  public dispose(): void {
    this.stop();

    if (this.visibilityHandler) {
      document.removeEventListener('visibilitychange', this.visibilityHandler);
    }

    if (this.particleMesh) {
      this.particleMesh.geometry.dispose();
      (this.particleMesh.material as THREE.Material).dispose();
    }

    if (this.trailLines) {
      this.trailLines.geometry.dispose();
      (this.trailLines.material as THREE.Material).dispose();
    }

    for (const [, mesh] of this.nodeMeshes) {
      mesh.geometry.dispose();
      (mesh.material as THREE.Material).dispose();
    }

    for (const line of this.edgeCurves) {
      line.geometry.dispose();
      (line.material as THREE.Material).dispose();
    }

    if (this.renderer) {
      this.renderer.dispose();
    }

    if (this.canvas && this.canvas.parentNode) {
      this.canvas.parentNode.removeChild(this.canvas);
    }

    if (this.labelContainer && this.labelContainer.parentNode) {
      this.labelContainer.parentNode.removeChild(this.labelContainer);
    }
  }
}
