// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

export interface INode {
  name: string;
  inclusion?: boolean;
}
export interface ILink {
  source: string;
  target: string;
  value: number;
}
export interface TxPool {
  pooledTx: number;
  pooledBlobTx: number;
  hashesReceived: number;
  links: ILink[];
}

export interface NodeData {
  uptime: number;
  instance: string;
  network: string;
  syncType: string;
  pruningMode: string;
  version: string;
  commit: string;
  runtime: string;
}

export interface Processed
{
  blockCount: number;
  blockFrom: number;
  blockTo: number;
  processingMs: number;
  slotMs: number;
  mgasPerSecond: number;
  minGas: number;
  medianGas: number;
  aveGas: number;
  maxGas: number;
  gasLimit: number;
}

export interface ForkChoice {
  head: number;
  safe: number;
  finalized: number;
}

export interface System {
  uptime: number,
  userPercent: number;
  privilegedPercent: number;
  workingSet: number;
}
