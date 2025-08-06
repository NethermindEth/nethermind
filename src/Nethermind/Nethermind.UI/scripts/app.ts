// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import {
  formatUnixTimestamp, formatBytes, parseExtraData, getNetworkName,
  getNetworkLogo, getNodeType, formatEth, formatDuration, format, formatDec,
  setGasToken
} from './format';
import { sparkline, Datum } from './sparkline';
import { NodeData, INode, TxPool, ForkChoice, System, TransactionReceipt, Peer } from './types';
import { TxPoolFlow } from './txPoolFlow';
import { updateTreemap } from './treeMap'
import { createRollingBoxPlot } from './boxPlot'
import { updatePieChart } from './peerPie'
import { getMethod, getTxType, getToAddress } from './txParser'
import { LogWindow } from './logWindow';
import { updateText } from './utilities';
import { GasInfo } from './gasInfo';

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

const blockExtraData = document.getElementById('blockExtraData') as HTMLElement;
const blockGasUsed = document.getElementById('blockGasUsed') as HTMLElement;
const blockGasLimit = document.getElementById('blockGasLimit') as HTMLElement;
const blockBlockSize = document.getElementById('blockBlockSize') as HTMLElement;
const blockTimestamp = document.getElementById('blockTimestamp') as HTMLElement;
const blockTransactions = document.getElementById('blockTransactions') as HTMLElement;
const blockBlobs = document.getElementById('blockBlobs') as HTMLElement;

// Create a rolling box plot instance for effectiveGasPrice
const boxPlotEGP = createRollingBoxPlot(
  document.getElementById('boxplot-effectiveGasPrice'),
  (d) => parseInt(d.effectiveGasPrice, 16) / 1_000_000_000, // convert to gwei
  36 // Keep up to 36 blocks
);


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

// Initialize the Sankey flow
const txPoolFlow = new TxPoolFlow('#txPoolFlow');


let txPoolNodes: INode[] = null;
/**
 * Main function to start polling data and updating the UI.
 */
let setActive = false;
function updateTxPool(txPool: TxPool) {

  if (!setActive) {
    if (txPool.pooledTx == 0) {
      document.getElementById("txPoolFlow").classList.add("not-active");
      return;
    }
    setTimeout(resize, 10);
    document.getElementById("txPoolFlow").classList.remove("not-active");
    setActive = true;
  }


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
    updateText(txPoolValue, format(txPool.pooledTx));
    updateText(blobTxPoolValue, format(txPool.pooledBlobTx));
    updateText(totalValue, format(txPool.pooledTx + txPool.pooledBlobTx));

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

const logWindow = new LogWindow("nodeLog");
const gasInfo = new GasInfo("minGas", "medianGas", "aveGas", "maxGas", "gasLimit", "gasLimitDelta");

const sse = new EventSource("/data/events");

sse.addEventListener("log", (e) => logWindow.receivedLog(e));
sse.addEventListener("processed", (e) => gasInfo.parseEvent(e));

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
  setGasToken(data.gasToken);
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

let lastBlockTime: number = 0;
let blockDelta: number = 0;
let setActiveBlock = false;
sse.addEventListener("forkChoice", (e) => {
  const blockTime = performance.now();
  blockDelta = blockTime - lastBlockTime;
  lastBlockTime = blockTime;

  if (document.hidden) return;

  const data = JSON.parse(e.data) as ForkChoice;
  const number = parseInt(data.head.number, 16);
  if (!setActiveBlock && number !== 0) {
    setActiveBlock = true;
    document.getElementById("lastestBlock").classList.remove("not-active");
    setTimeout(resize, 10);
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
    return { block: block.number, order: i, ...tx, ...receipt };
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
    160,
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

  txsToAdd.push(...mergedData);
  lastBlockTxs = txsToAdd.length;

  if (txsToAdd.length > 250000) txsToAdd.slice(txsToAdd.length - 25000);
});

let lastBlockTxs:number = 0;
let txsToAdd: TransactionReceipt[] = [];

sse.addEventListener("peers", (e) => {
  if (document.hidden) return;

  const data = JSON.parse(e.data) as Peer[];

  // Aggregate by clientType
  let countsByType: { [k: string]: number } = data.reduce((acc, peer) => {
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
  if (document.hidden) return;

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


  updateText(upTime, formatDuration(data.uptime));

  updateText(cpuTime, formatDec(cpuPercent));
  updateText(maxCpuTime, formatDec(maxCpuPercent));
  sparkline(sparkCpu, seriesTotalCpu, 150, 100, 60);

  updateText(memory, format(memoryMb));
  updateText(maxMemory, format(maxMemoryMb));
  sparkline(sparkMemory, seriesMemory, 150, 100, 60);
});



let lastFrameTime: number = 0;
let frameDelta: number = 0;
function newFrame() {
  requestAnimationFrame(newFrame);
  if (document.hidden) return;

  const frameTime = performance.now();
  frameDelta = frameTime - lastFrameTime;

  logWindow.appendLogs();
  appendTxs();

  lastFrameTime = frameTime;
}

let lastTxBlock = "";
let evenTx = true;
function appendTxs() {
  if (txsToAdd.length === 0) return;

  let maxTxToAdd = Math.min(Math.round(lastBlockTxs * ((performance.now() - lastBlockTime) / blockDelta)) - (lastBlockTxs - txsToAdd.length), txsToAdd.length);
  if (maxTxToAdd <= 0) return;

  const txs = document.getElementById("blockTxs");

  if (maxTxToAdd > 250) {
    const skip = maxTxToAdd - 250;
    maxTxToAdd = 250;

    txsToAdd = txsToAdd.slice(skip);
  }

  let newTxs = "";
  for (var i = 0; i < maxTxToAdd; i++) {
    const tx = txsToAdd.shift();
    if (!tx) break;

    if (lastTxBlock !== tx.block) {
      newTxs += `<tr class="blockHeader"><td colspan="7">Block ${parseInt(tx.block,16)}</td></tr>`;
    }

    const to = getToAddress(tx.to);
    newTxs += `<tr ${evenTx ? 'class="even"' : ''}><td>${tx.status == "0x1" ? "🟢" : "⭕"}</td><td>${tx.from.slice(0, 6)}...${tx.from.slice(tx.from.length - 4)}</td>`
      + (to && to.length > 11 ? `<td>${to.slice(0, 6)}...${to.slice(to.length - 4)}</td>` :
      to ? `<td>${to}</td>` : "<td>Create</td>")
      + `<td class="right">${formatEth(parseInt(tx.value, 16))}</td><td class="right">${parseInt(tx.gasUsed, 16)}</td><td>${getMethod(tx.method)}</td><td>${getTxType(tx.txType)}</td></tr>`;

    evenTx = !evenTx;
    lastTxBlock = tx.block;
  }

  txs.insertAdjacentHTML("afterbegin", newTxs);

  let rows = txs.childElementCount;
  while (rows > 250) {
    txs.lastChild.remove();
    rows--;
  }
}

requestAnimationFrame(newFrame);
// On window resize
window.addEventListener('resize', () => {
  resize();
});

function resize() {
  boxPlotEGP.resize();
  logWindow.resize();

  const txLog = document.getElementById("txLog");
  const bodyRect = document.body.getBoundingClientRect(), elemRect = txLog.getBoundingClientRect();

  const offset = elemRect.top - bodyRect.top;
  const height = window.innerHeight - offset - 16;

  if (height > 0) {
    txLog.style.height = `${height}px`;
  }
}
