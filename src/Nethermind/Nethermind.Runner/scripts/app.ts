// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';
import Convert = require('ansi-to-html');
import { formatDuration } from './format';
import { sparkline, Datum } from './sparkline';
import { NodeData, INode, TxPool, Processed, ForkChoice, System, TransactionReceipt, Peer } from './types';
import { TxPoolFlow } from './txPoolFlow';
import { updateTreemap } from './treeMap'
import { formatUnixTimestamp, formatBytes, parseExtraData, getNetworkName, getNetworkLogo, getNodeType } from './utilities';
import { createRollingBoxPlot } from './boxPlot'
import { updatePieChart } from './peerPie'

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

const blockExtraData = document.getElementById('blockExtraData') as HTMLElement;
const blockGasUsed = document.getElementById('blockGasUsed') as HTMLElement;
const blockGasLimit = document.getElementById('blockGasLimit') as HTMLElement;
const blockBlockSize = document.getElementById('blockBlockSize') as HTMLElement;
const blockTimestamp = document.getElementById('blockTimestamp') as HTMLElement;
const blockTransactions = document.getElementById('blockTransactions') as HTMLElement;
const blockBlobs = document.getElementById('blockBlobs') as HTMLElement;

//const ridgeDataLength = createRidgelinePlot(
//  d3.select('#ridgeline-dataLength'),
//  (d) => Math.log10( d.dataLength )// numeric field in the TxReceipt
//);

//const ridgeValue = createRidgelinePlot(
//  d3.select('#ridgeline-value'),
//  (d) => Math.log10(parseInt(d.value, 16) / 1000000000000000000) // parse from hex
//);

//const ridgeGasUsed = createRidgelinePlot(
//  d3.select('#ridgeline-gasUsed'),
//  (d) => Math.log10(parseInt(d.gasUsed, 16))
//);

//const ridgeEffectiveGasPrice = createRidgelinePlot(
//  d3.select('#ridgeline-effectiveGasPrice'),
//  (d) => Math.log10(parseInt(d.effectiveGasPrice, 16) / 1000000000)
//);

// Create a rolling box plot instance for effectiveGasPrice
const boxPlotEGP = createRollingBoxPlot(
  document.getElementById('boxplot-effectiveGasPrice'),
  (d) => parseInt(d.effectiveGasPrice, 16) / 1_000_000_000, // convert to gwei
  36 // Keep up to 36 blocks
);

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

function addCapped(array: Datum[], datum: Datum) {
  array.push(datum);
  if (array.length > 60) {
    array.shift();
  }
}

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

  if (txPool.pooledTx == 0) {
    document.getElementById("txPoolFlow").classList.add("not-active");
    return;
  }
  document.getElementById("txPoolFlow").classList.remove("not-active");

  const nowMs = performance.now();
  const currentNow = nowMs / 1000;
  const diff = currentNow - lastNow;

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

  if (lastNow !== 0) {
    addCapped(seriesHashes, { t: nowMs, v: currentHashesReceived - lastHashesReceived });
    addCapped(seriesReceived, { t: nowMs, v: currentReceived - lastReceived });
    addCapped(seriesDuplicate, { t: nowMs, v: currentDuplicate - lastDuplicate });
    addCapped(seriesTxPool, { t: nowMs, v: currentTxPool - lastTxPool });
    addCapped(seriesBlock, { t: nowMs, v: currentBlock - lastBlock });
  }


  if (!document.hidden) {
    if (!txPoolNodes) {
      return;
    }
    // Update Sankey
    txPoolFlow.update(txPoolNodes, txPool);

    // Update numeric indicators
    updateText(txPoolValue, d3.format(',.0f')(txPool.pooledTx));
    updateText(blobTxPoolValue, d3.format(',.0f')(txPool.pooledBlobTx));
    updateText(totalValue, d3.format(',.0f')(txPool.pooledTx + txPool.pooledBlobTx));

    if (lastNow !== 0) {
      // Update the sparkline for each type
      sparkline(document.getElementById('sparkHashesTps') as HTMLElement, seriesHashes);
      sparkline(document.getElementById('sparkReceivedTps') as HTMLElement, seriesReceived);
      sparkline(document.getElementById('sparkDuplicateTps') as HTMLElement, seriesDuplicate);
      sparkline(document.getElementById('sparkTxPoolTps') as HTMLElement, seriesTxPool);
      sparkline(document.getElementById('sparkBlockTps') as HTMLElement, seriesBlock);

      // Show TPS values
      updateText(blockTpsValue, formatDec((currentBlock - lastBlock) / diff));
      updateText(receivedTpsValue, formatDec((currentReceived - lastReceived) / diff));
      updateText(txPoolTpsValue, formatDec((currentTxPool - lastTxPool) / diff));
      updateText(duplicateTpsValue, formatDec((currentDuplicate - lastDuplicate) / diff));
      updateText(hashesReceivedTpsValue, formatDec((currentHashesReceived - lastHashesReceived) / diff));
    }
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

  const networkName = getNetworkName(data.network);
  const newTitle = `Nethermind [${networkName}]${(data.instance ? ' - ' + data.instance : '')}`;
  if (document.title != newTitle) {
    document.title = newTitle;
  }
  updateText(version, data.version);
  updateText(network, networkName);
  (document.getElementById("network-logo") as HTMLImageElement).src = `logos/${getNetworkLogo(data.network)}`;
  // Update uptime text
  updateText(upTime, formatDuration(data.uptime));
});
sse.addEventListener("txNodes", (e) => {
  const data = JSON.parse(e.data) as INode[];
  txPoolNodes = data;
});
sse.addEventListener("txLinks", (e) => {
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
  const number = parseInt(data.head.number, 16);
  if (number !== 0) {
    document.getElementById("lastestBlock").classList.remove("not-active");
    //document.getElementById("recentBlocks").classList.remove("not-active");
  }
  const safe = parseInt(data.safe, 16);
  const finalized = parseInt(data.finalized, 16);
  updateText(headBlock, number.toFixed(0));
  updateText(safeBlock, safe.toFixed(0));
  updateText(finalizedBlock, finalized.toFixed(0));

  updateText(safeBlockDelta, `(${(safe - number).toFixed(0)})`);
  updateText(finalizedBlockDelta, `(${(finalized - number).toFixed(0)})`);

  const block = data.head;

  if (block.tx.length === 0) return;
  // Merge tx & receipts into a single array
  // so each element has { key, size, colorValue, ... } etc.
  // (One data item per transaction)
  const mergedData: TransactionReceipt[] = block.tx.map((tx, i) => {
    const receipt = block.receipts[i];
    return { order: i, ...tx, ...receipt };
  });

  updateText(blockExtraData, parseExtraData(block.extraData));
  updateText(blockGasUsed, format(parseInt(block.gasUsed, 16)));
  updateText(blockGasLimit, format(parseInt(block.gasLimit, 16)));
  updateText(blockBlockSize, formatBytes(parseInt(block.size, 16)));
  updateText(blockTimestamp, formatUnixTimestamp(parseInt(block.timestamp, 16)));
  updateText(blockTransactions, format(block.tx.length));
  updateText(blockBlobs, format(block.tx.reduce((p, c) => p + c.blobs, 0)));

  // Update the D3 treemap with the merged data
  updateTreemap<TransactionReceipt>(
    document.getElementById("block"), // or d3.select("#treemap")
    200,
    parseInt(data.head.gasLimit, 16),
      mergedData,
      // keyFn
      d => d.hash,
      // orderFn
      d => d.order,
      // sizeFn
      d => parseInt(d.gasUsed, 16),
      // colorFn
      d => parseInt(d.effectiveGasPrice, 16) * parseInt(d.gasUsed, 16)
  );

  // Update the rolling box plot for this block
  // passing the blockNum as well:
  boxPlotEGP.update(mergedData, number);
});

sse.addEventListener("peers", (e) => {
  const data = JSON.parse(e.data) as Peer[];

  // e.data is a JSON array of Peer objects
  const peersArray = JSON.parse(e.data) as Peer[];

  // Aggregate by clientType
  let countsByType = data.reduce((acc, peer) => {
    let nodeType = getNodeType(peer.clientType);
    acc[nodeType] = (acc[nodeType] || 0) + 1;
    return acc;
  }, {});

  // Convert to an array for D3
  const peers = Object.entries(countsByType).map(([type, count]) => ({
    type,
    count
  }));

  // Now update the chart
  updatePieChart(peers);
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

  const now = performance.now();
  addCapped(seriesTotalCpu, { t: now, v: data.userPercent + data.privilegedPercent });
  addCapped(seriesMemory, { t: now, v: memoryMb });

  if (document.hidden) return;

  updateText(upTime, formatDuration(data.uptime));

  updateText(cpuTime, formatDec(cpuPercent));
  updateText(maxCpuTime, formatDec(maxCpuPercent));
  sparkline(sparkCpu, seriesTotalCpu, 150, 100, 60);

  updateText(memory, format(memoryMb));
  updateText(maxMemory, format(maxMemoryMb));
  sparkline(sparkMemory, seriesMemory, 150, 100, 60);
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
// On window resize
window.addEventListener('resize', () => {
  boxPlotEGP.resize();
});
