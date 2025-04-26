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

let logs: string[] = [];
sse.addEventListener("log", (e) => {
  const data = JSON.parse(e.data) as string[];
  for (let entry of data) {
    const html = ansiConvert.toHtml(entry);
    if (logs.length > 100) { logs.shift(); }
    logs.push(html);
  }
});

let lastFrameTime: number = 0;
let frameDelta: number = 0;
function newFrame() {
  requestAnimationFrame(newFrame);
  if (document.hidden) return;

  const frameTime = performance.now();
  frameDelta = frameTime - lastFrameTime;

  appendLogs();
  appendTx();

  lastFrameTime = frameTime;
}
function appendLogs() {
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
    let rows = nodeLog.childElementCount;
    while (rows > 250) {
      nodeLog.firstChild.remove();
      rows--;
    }

    if (scroll) {
      window.setTimeout(scrollLogs, 17);
    }
  }
}

let lastTxBlock = "";
let evenTx = true;
function appendTx() {
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

function getTxType(type: number): string {
  switch (type) {
    case 0: return "Legacy";
    case 1: return "AccessList";
    case 2: return "Eip1559";
    case 3: return "Blob";
    case 4: return "SetCode";
    case 5: return "TxCreate";
    default: return "Unknown";
  }
}
function getMethod(type: string): string {
  switch (type) {
    case "0x095ea7b3": return "approve";
    case "0xa9059cbb": return "transfer";
    case "0x23b872dd": return "transferFrom";
    case "0xd0e30db0": return "deposit";
    case "0xe8e33700": // addLiquidity(0xe8e33700)
    case "0xf305d719": return "addLiquidity"; // addLiquidityETH(0xf305d719)
    case "0xbaa2abde": // removeLiquidity(0xbaa2abde)
    case "0x02751cec": // removeLiquidityETH(0x02751cec)
    case "0xaf2979eb": // removeLiquidityETHSupportingFeeOnTransferTokens(0xaf2979eb)
    case "0xded9382a": // removeLiquidityETHWithPermit(0xded9382a)
    case "0x5b0d5984": // removeLiquidityETHWithPermitSupportingFeeOnTransferTokens(0x5b0d5984)
    case "0x2195995c": return "removeLiquidity"; // removeLiquidityWithPermit(0x2195995c)
    case "0xfb3bdb41": // swapETHForExactTokens(0xfb3bdb41)
    case "0x7ff36ab5": // swapExactETHForTokens(0x7ff36ab5)
    case "0xb6f9de95": // swapExactETHForTokensSupportingFeeOnTransferTokens(0xb6f9de95)
    case "0x18cbafe5": // swapExactTokensForETH(0x18cbafe5)
    case "0x791ac947": // swapExactTokensForETHSupportingFeeOnTransferTokens(0x791ac947)
    case "0x38ed1739": // swapExactTokensForTokens(0x38ed1739)
    case "0x5c11d795": // swapExactTokensForTokensSupportingFeeOnTransferTokens(0x5c11d795)
    case "0x4a25d94a": // swapTokensForExactETH(0x4a25d94a)
    case "0x5f575529": // swap(0x5f575529)
    case "0x6b68764c": // swapUsingGasToken (0x6b68764c)
    case "0x845a101f": // swap (0x845a101f)
    case "0x8803dbee": return "swap"; // swapTokensForExactTokens(0x8803dbee)
    case "0x24856bc3": // execute (0x24856bc3)
    case "0x3593564c": return "dex"; // execute (0x3593564c)
    case "0x": return "ETH transfer";
    default: return type;
  }
}
function getToAddress(to: string): string {
  switch (to) {
    case "0xdac17f958d2ee523a2206206994597c13d831ec7": return "usdt"; // mainnet
    case "0x4ecaba5870353805a9f068101a40e0f32ed605c6": return "usdt"; // Gnosis
    case "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48": return "usdc"; // mainnet
    case "0xddafbb505ad214d7b80b1f830fccc89b60fb7a83": return "usdc"; // Gnosis
    case "0x6b175474e89094c44da98b954eedeac495271d0f": return "dai"; // mainnet
    case "0x44fa8e6f47987339850636f88629646662444217": return "dai"; // Gnosis
    case "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2": return "weth"; // mainnet
    case "0x6a023ccd1ff6f2045c3309768ead9e68f978f6e1": return "weth"; // Gnosis
    case "0x7a250d5630b4cf539739df2c5dacb4c659f2488d": return "uniswap v2"; // mainnet
    case "0x66a9893cc07d91d95644aedd05d03f95e1dba8af": return "uniswap v4"; // mainnet
    case "0x2f848984984d6c3c036174ce627703edaf780479": return "xen minter"; // mainnet
    case "0x0a252663dbcc0b073063d6420a40319e438cfa59": return "xen" // mainnet
    case "0x881d40237659c251811cec9c364ef91dc08d300c": return "metamask" // mainnet
    default: return to;
  }
}

requestAnimationFrame(newFrame);
function scrollLogs() {
  nodeLog.scrollTop = nodeLog.scrollHeight;
}
// On window resize
window.addEventListener('resize', () => {
  resize();
});

function resize() {
    boxPlotEGP.resize();

    var bodyRect = document.body.getBoundingClientRect(), elemRect = nodeLog.getBoundingClientRect();

    var offset = elemRect.top - bodyRect.top;
    var height = window.innerHeight - offset - 16;

    if (height > 0) {
        nodeLog.style.height = `${height}px`;
        document.getElementById("txLog").style.height = `${height}px`;
    }
}

/**
 * Formats a number (wei) into a string with the largest possible unit
 * (ETH, GWEI, or WEI) and up to 4 decimal digits.
 *
 * @param weiValue - The numeric value in wei (e.g., from parseInt(tx.value, 16)).
 * @returns The value formatted as a string with the appropriate unit.
 */
function formatEth(weiValue: number): string {
  // 1 ETH = 1e18 wei
  const WEI_IN_ETH = 1e18;
  // 1 GWEI = 1e9 wei
  const WEI_IN_GWEI = 1e9;

  let result: string;
  if (weiValue == 0) result = "-";
  else if (weiValue >= 100_000_000_000_000) {
    // Convert wei to ETH
    const ethValue = weiValue / WEI_IN_ETH;
    result = `${trimDecimals(ethValue, 4)} ETH`;
  } else if (weiValue >= 100_000) {
    // Convert wei to GWEI
    const gweiValue = weiValue / WEI_IN_GWEI;
    result = `${trimDecimals(gweiValue, 4)} GWEI`;
  } else {
    // Value is small enough to stay in wei
    result = `${weiValue} WEI`;
  }

  return result;
}

/**
 * Helper to trim a numeric value to a max of `decimals` decimal digits,
 * removing trailing zeroes after the decimal point.
 *
 * @param value - The number to trim.
 * @param decimals - Max number of decimal digits allowed.
 */
function trimDecimals(value: number, decimals: number): string {
  // toFixed will limit the decimal places, but may produce trailing zeros
  const fixed = value.toFixed(decimals);
  // Remove trailing zeros and the decimal point if not needed
  return fixed.replace(/(\.\d*?[1-9])0+$/g, '$1').replace(/\.0+$/, '');
}
