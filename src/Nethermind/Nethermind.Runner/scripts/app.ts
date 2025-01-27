// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';
import Convert = require('ansi-to-html');
import { formatDuration } from './format';
import { sparkline, Datum } from './sparkline';
import { NodeData, INode, TxPool, Processed, ForkChoice, System } from './types';
import { TxPoolFlow } from './txPoolFlow';

// Grab DOM elements
const txPoolValue = document.getElementById('txPoolValue') as HTMLElement;
const blobTxPoolValue = document.getElementById('blobTxPoolValue') as HTMLElement;
const totalValue = document.getElementById('totalValue') as HTMLElement;

const blockTpsValue = document.getElementById('blockTpsValue') as HTMLElement;
const receivedTpsValue = document.getElementById('receivedTpsValue') as HTMLElement;
const txPoolTpsValue = document.getElementById('txPoolTpsValue') as HTMLElement;
const duplicateTpsValue = document.getElementById('duplicateTpsValue') as HTMLElement;
const hashesReceivedTpsValue = document.getElementById('hashesReceivedTpsValue') as HTMLElement;
const version = document.getElementById('version') as HTMLElement;

const upTime = document.getElementById('upTime') as HTMLElement;
const network = document.getElementById('network') as HTMLElement;
const nodeLog = document.getElementById('nodeLog') as HTMLElement;
const headBlock = document.getElementById('headBlock') as HTMLElement;
const safeBlock = document.getElementById('safeBlock') as HTMLElement;
const finalizedBlock = document.getElementById('finalizedBlock') as HTMLElement;
const safeBlockDelta = document.getElementById('safeBlockDelta') as HTMLElement;
const finalizedBlockDelta = document.getElementById('finalizedBlockDelta') as HTMLElement;

const sparkCpu = document.getElementById('sparkCpu') as HTMLElement;
const cpuTime = document.getElementById('cpuTime') as HTMLElement;
const maxCpuTime = document.getElementById('maxCpuTime') as HTMLElement;

const sparkMemory = document.getElementById('sparkMemory') as HTMLElement;
const memory = document.getElementById('memory') as HTMLElement;
const maxMemory = document.getElementById('maxMemory') as HTMLElement;

const minGas = document.getElementById('minGas') as HTMLElement;
const medianGas = document.getElementById('medianGas') as HTMLElement;
const aveGas = document.getElementById('aveGas') as HTMLElement;
const maxGas = document.getElementById('maxGas') as HTMLElement;
const gasLimit = document.getElementById('gasLimit') as HTMLElement;
const gasLimitDelta = document.getElementById('gasLimitDelta') as HTMLElement;

const ansiConvert = new Convert();

// We reuse these arrays for the sparkline. The length = 60 means we store 60 historical points.
let seriesHashes: Datum[] = [];
let seriesReceived: Datum[] = [];
let seriesTxPool: Datum[] = [];
let seriesBlock: Datum[] = [];
let seriesDuplicate: Datum[] = [];

let seriesTotalCpu: Datum[] = [];
let seriesMemory: Datum[] = [];

// Keep track of last values so we can compute TPS
let lastReceived = 0;
let lastTxPool = 0;
let lastBlock = 0;
let lastDuplicate = 0;
let lastHashesReceived = 0;
let lastNow = 0;

function updateText(element: HTMLElement, value: string): void {
  if (element.innerText !== value) {
    // Don't update the DOM if the value is the same
    element.innerText = value;
  }
}

// Initialize the Sankey flow
const txPoolFlow = new TxPoolFlow('#txPoolFlow');

// Number format
const format = d3.format(',.0f');
const formatDec = d3.format(',.1f');

let txPoolNodes: INode[] = null;
/**
 * Main function to start polling data and updating the UI.
 */
function updateTxPool(txPool: TxPool) {

  if (!txPoolNodes) {
    return;
  }
  // Update Sankey
  txPoolFlow.update(txPoolNodes, txPool);

  // Update numeric indicators
  updateText(txPoolValue, d3.format(',.0f')(txPool.pooledTx));
  updateText(blobTxPoolValue, d3.format(',.0f')(txPool.pooledBlobTx));
  updateText(totalValue, d3.format(',.0f')(txPool.pooledTx + txPool.pooledBlobTx));

  // Summarize link flows to compute TPS
  let currentReceived = 0;
  let currentTxPool = 0;
  let currentBlock = 0;
  let currentDuplicate = 0;

  for (const link of txPool.links) {
    if (link.target === 'Received Txs') {
      currentReceived += link.value;
    }
    if (link.target === 'Tx Pool') {
      currentTxPool += link.value;
    }
    if (link.target === 'Added To Block') {
      currentBlock += link.value;
    }
    if (link.target === 'Duplicate') {
      currentDuplicate += link.value;
    }
  }
  const currentHashesReceived = txPool.hashesReceived;
  const nowMs = performance.now();
  const currentNow = nowMs / 1000;

  if (lastNow !== 0) {
    const diff = currentNow - lastNow;

    // Update the sparkline for each type
    sparkline(document.getElementById('sparkHashesTps') as HTMLElement,
      seriesHashes, { t: nowMs, v: currentHashesReceived - lastHashesReceived });
    sparkline(document.getElementById('sparkReceivedTps') as HTMLElement,
      seriesReceived, { t: nowMs, v: currentReceived - lastReceived });
    sparkline(document.getElementById('sparkDuplicateTps') as HTMLElement,
      seriesDuplicate, { t: nowMs, v: currentDuplicate - lastDuplicate });
    sparkline(document.getElementById('sparkTxPoolTps') as HTMLElement,
      seriesTxPool, { t: nowMs, v: currentTxPool - lastTxPool });
    sparkline(document.getElementById('sparkBlockTps') as HTMLElement,
      seriesBlock, { t: nowMs, v: currentBlock - lastBlock });

    // Show TPS values
    updateText(blockTpsValue, formatDec((currentBlock - lastBlock) / diff));
    updateText(receivedTpsValue, formatDec((currentReceived - lastReceived) / diff));
    updateText(txPoolTpsValue, formatDec((currentTxPool - lastTxPool) / diff));
    updateText(duplicateTpsValue, formatDec((currentDuplicate - lastDuplicate) / diff));
    updateText(hashesReceivedTpsValue, formatDec((currentHashesReceived - lastHashesReceived) / diff));
  }

  // Update "last" values for next iteration
  lastNow = currentNow;
  lastReceived = currentReceived;
  lastTxPool = currentTxPool;
  lastBlock = currentBlock;
  lastDuplicate = currentDuplicate;
  lastHashesReceived = currentHashesReceived;
}

const sse = new EventSource("/data/events");
sse.addEventListener("nodeData", (e) => {
  const data = JSON.parse(e.data) as NodeData;

  var newTitle = `Nethermind [${data.network}]${(data.instance ? ' - ' + data.instance : '')}`;
  if (document.title != newTitle) {
    document.title = newTitle;
  }
  updateText(version, data.version);
  updateText(network, data.network);
  // Update uptime text
  updateText(upTime, formatDuration(data.uptime));
});
sse.addEventListener("txNodes", (e) => {
  const data = JSON.parse(e.data) as INode[];
  txPoolNodes = data;
});
sse.addEventListener("txLinks", (e) => {
  if (document.hidden) return;
  const data = JSON.parse(e.data) as TxPool;
  updateTxPool(data);
});
let lastGasLimit = 30_000_000;
sse.addEventListener("processed", (e) => {
  if (document.hidden) return;
  const data = JSON.parse(e.data) as Processed;

  updateText(minGas, data.minGas.toFixed(2));
  updateText(medianGas, data.medianGas.toFixed(2));
  updateText(aveGas, data.aveGas.toFixed(2));
  updateText(maxGas, data.maxGas.toFixed(2));
  updateText(gasLimit, format(data.gasLimit));
  updateText(gasLimitDelta, data.gasLimit > lastGasLimit ? '👆' : data.gasLimit < lastGasLimit ? '👇' : '👈');

  lastGasLimit = data.gasLimit;
});
sse.addEventListener("forkChoice", (e) => {

  if (document.hidden) return;

  const data = JSON.parse(e.data) as ForkChoice;
  updateText(headBlock, data.head.toFixed(0));
  updateText(safeBlock, data.safe.toFixed(0));
  updateText(finalizedBlock, data.finalized.toFixed(0));

  updateText(safeBlockDelta, `(${(data.safe - data.head).toFixed(0)})`);
  updateText(finalizedBlockDelta, `(${(data.finalized - data.head).toFixed(0)})`);
});
let maxCpuPercent = 0;
let maxMemoryMb = 0;
sse.addEventListener("system", (e) => {
  const data = JSON.parse(e.data) as System;
  let memoryMb = data.workingSet / (1024 * 1024);
  if (memoryMb > maxMemoryMb) {
    maxMemoryMb = memoryMb;
  }
  let cpuPercent = (data.userPercent + data.privilegedPercent) * 100;
  if (cpuPercent > maxCpuPercent) {
    maxCpuPercent = cpuPercent;
  }

  if (document.hidden) return;

  updateText(upTime, formatDuration(data.uptime));

  updateText(cpuTime, formatDec(cpuPercent));
  updateText(maxCpuTime, formatDec(maxCpuPercent));
  sparkline(sparkCpu, seriesTotalCpu, { t: performance.now(), v: data.userPercent + data.privilegedPercent }, 300, 100, 60);

  updateText(memory, format(memoryMb));
  updateText(maxMemory, format(maxMemoryMb));
  sparkline(sparkMemory, seriesMemory, { t: performance.now(), v: memoryMb }, 300, 100, 60);
});

let logs: string[] = [];
sse.addEventListener("log", (e) => {
  const data = JSON.parse(e.data) as string[];
  for (let entry of data) {
    const html = ansiConvert.toHtml(entry);
    if (logs.length > 100) { logs.shift(); }
    logs.push(html);
  }
});

function appendLogs() {
  requestAnimationFrame(appendLogs);
  if (logs.length > 0) {
    let scroll = false;
    if (nodeLog.scrollHeight < 500 || nodeLog.scrollTop < nodeLog.scrollHeight - 500) {
      scroll = true;
    }
    const frag = document.createDocumentFragment();
    for (let i = 0; i < logs.length; i++) {
      const newEntry = document.createElement('div');
      newEntry.innerHTML = logs[i];
      frag.appendChild(newEntry);
    }
    logs = [];
    nodeLog.appendChild(frag);
    if (scroll) {
      window.setTimeout(scrollLogs, 17);
    }
  }
}

requestAnimationFrame(appendLogs);
function scrollLogs() {
  nodeLog.scrollTop = nodeLog.scrollHeight;
}
