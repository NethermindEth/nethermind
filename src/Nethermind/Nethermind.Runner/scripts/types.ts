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
  head: Block;
  safe: string;
  finalized: string;
}

export interface Block {
  extraData: string;
  gasLimit: string;
  gasUsed: string;
  hash: string;
  beneficiary: string;
  number: string;
  size: string;
  timestamp: string;
  baseFeePerGas: string;
  blobGasUsed: string;
  excessBlobGas: string;
  tx: Transaction[]; // matched to receipts 1:1
  receipts: Receipt[]; // matched to tx 1:1
}
export interface Transaction {
  hash: string;
  from: string;
  to: string;
  txType: number;
  maxPriorityFeePerGas: string;
  maxFeePerGas: string;
  gasPrice: string;
  gasLimit: string;
  nonce: string;
  value: string;
  dataLength: number;
  blobs: number;
  method: string;
}
export interface Receipt {
  gasUsed: string;
  effectiveGasPrice: string;
  contractAddress: string;
  blobGasPrice: string;
  blobGasUsed: string;
  logs: Log[];
  status: string;
}

export interface TransactionReceipt extends Transaction, Receipt {
  block: string,
  order: number
}
export interface Log {
  address: string;
  data: string;
  topics: string[];
}

export interface System {
  uptime: number;
  userPercent: number;
  privilegedPercent: number;
  workingSet: number;
}

export interface Peer {
  contexts: number;
  clientType: number;
  version: number;
  head: number;
}
