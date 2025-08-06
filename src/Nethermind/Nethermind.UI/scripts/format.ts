// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


import { format as formatD3 } from 'd3';

// Number format
export const format = formatD3(',.0f');
export const formatDec = formatD3(',.1f');

/**
 * Formats a duration (in milliseconds) as d h m s, or h m s, etc.
 */
export function formatDuration(ms: number): string {
  function pad(num: number): string {
    return num.toString().padStart(2, '0');
  }

  let totalSeconds = Math.floor(ms / 1000);
  let totalMinutes = Math.floor(totalSeconds / 60);
  let totalHours = Math.floor(totalMinutes / 60);

  let days = Math.floor(totalHours / 24);
  let hours = totalHours % 24;
  let minutes = totalMinutes % 60;
  let seconds = totalSeconds % 60;

  if (days === 0 && hours === 0 && minutes === 0 && seconds === 0) {
    return '0s';
  }

  if (days > 0) {
    return `${days}d ${pad(hours)}h ${pad(minutes)}m ${pad(seconds)}s`;
  }

  if (hours > 0) {
    return `${hours}h ${pad(minutes)}m ${pad(seconds)}s`;
  }

  if (minutes > 0) {
    return `${minutes}m ${pad(seconds)}s`;
  }

  return `${seconds}s`;
}

const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export function formatUnixTimestamp(timestampInSeconds: number): string {
  // Convert the Unix timestamp (seconds) to milliseconds
  const date = new Date(timestampInSeconds * 1000);

  // Prepare day, month, year, hours, minutes, seconds
  const day = String(date.getDate()).padStart(2, '0');
  const month = months[date.getMonth()];
  const year = date.getFullYear();
  const hours = String(date.getHours()).padStart(2, '0');
  const minutes = String(date.getMinutes()).padStart(2, '0');
  const seconds = String(date.getSeconds()).padStart(2, '0');

  // Return the formatted date string
  // Example: "09 Feb 2025 13:45:00"
  return `${day} ${month} ${year} ${hours}:${minutes}:${seconds}`;
}

const units = ["bytes", "kB", "MB", "GB", "TB", "PB"];

export function formatBytes(bytes: number): string {
  // Handle negative or NaN gracefully (could also throw an error if needed)
  if (!Number.isFinite(bytes) || bytes < 0) {
    return "0 bytes";
  }

  // Start with the base unit
  let unitIndex = 0;
  let value = bytes;

  // Divide down until the value is < 1000 or we reach the last unit
  while (value >= 1000 && unitIndex < units.length - 1) {
    value /= 1000;
    unitIndex++;
  }

  // Round to one decimal place
  let rounded = parseFloat(value.toFixed(1));

  // If rounding gave us 1000.0, move to the next unit (if possible)
  if (rounded === 1000 && unitIndex < units.length - 1) {
    rounded = 1.0;
    unitIndex++;
  }

  // If the rounded value is actually an integer, we may want to show
  // no decimal places (e.g. "1 kB" instead of "1.0 kB").
  // Adjust to your preference. Here we drop the .0 if integer:
  const roundedStr =
    rounded % 1 === 0 ? `${rounded.toFixed(0)}` : `${rounded.toFixed(1)}`;

  return `${roundedStr} ${units[unitIndex]}`;
}

/**
 * Converts a hex string (with "0x" prefix) into a human-readable string:
 *  - If the data decodes to (mostly) valid text, returns "Extra Data: <the text>".
 *  - Otherwise, returns "Hex: 0x<original hex>".
 * 
 * @param extraData A hex string starting with "0x".
 * @returns A string describing either the UTF-8 interpretation or the raw hex.
 */
export function parseExtraData(extraData: string | null | undefined): string {
  if (!extraData || extraData === "0x") {
    return "0x";
  }

  // Remove the "0x" prefix if present
  let hex = extraData.startsWith("0x")
    ? extraData.slice(2)
    : extraData;

  // If there is nothing after removing "0x", return "0x"
  if (hex.length === 0) {
    return "0x";
  }

  // Convert hex -> bytes
  const bytes = hexStringToUint8Array(hex);

  // Decode to UTF-8
  let decoder = new TextDecoder('utf-8', { fatal: false });
  let decoded = decoder.decode(bytes);

  // Count the number of control characters
  let controlCount = decoded.split('').filter(c => isControlCharacter(c.charCodeAt(0))).length;

  // Return the original hex string if there are too many control characters
  if (controlCount >= decoded.length / 2) {
    return `0x${hex}`;
  }

  // Return the decoded string
  return decoded;
}

/**
 * Check if the code point is a "control character" that we want to skip.
 * 
 * You can adapt this check if you only want to skip ASCII controls < 0x20, etc.
 */
function isControlCharacter(codePoint: number): boolean {
  // This is a simple ASCII-based check. 
  // The .NET version of Rune.IsControl might do more comprehensive checks.
  if (codePoint < 0x20) {
    return true;  // Basic control range
  }
  if (codePoint >= 0x7f && codePoint < 0xa0) {
    return true;  // DEL and extended ASCII control range
  }
  return false;
}

/**
 * Converts a hex string (without "0x") to a Uint8Array.
 */
function hexStringToUint8Array(hex: string): Uint8Array {
  if (hex.length === 0 || hex.length % 2 !== 0) {
    // In real code, handle error or return empty array if invalid hex
    return new Uint8Array();
  }

  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) {
    bytes[i / 2] = parseInt(hex.substring(i, i + 2), 16);
  }
  return bytes;
}

const networks = {
  "Mainnet": "Ethereum mainnet",
  "Gnosis": "Gnosis mainnet",
  "1": "Ethereum mainnet",
  "100": "Gnosis Mainnet",
  "480": "World Mainnet",
  "8453": "Base mainnet",
  "10200": "Gnosis Chiado Testnet",
  "7032118028": "Ethereum Perfnet"
}
const logos = {
  "Mainnet": "ethereum-logo.svg",
  "Gnosis": "gnosis.png",
  "1": "ethereum-logo.svg",
  "100": "gnosis.png",
  "480": "world-logo.svg",
  "8453": "base-logo.svg",
  "10200": "gnosis.png",
  "7032118028": "perfnet.png"
}

export function getNetworkName(network: string) {
  return networks[network] || network;
}
export function getNetworkLogo(network: string) {
  return logos[network] || "unknown.png";
}

const clientTypes = [
  "Unknown",
  "Besu",
  "Geth",
  "Nethermind",
  "Parity",
  "OpenEthereum",
  "Trinity",
  "Erigon",
  "Reth",
  "Nimbus",
  "EthereumJS"
];
export function getNodeType(clientType: number) {
  return clientTypes[clientType] || "Unknown";
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

let _gasToken: string = "ETH";
export function setGasToken(gasToken: string) {
  if (gasToken) {
    _gasToken = gasToken;
  }
}
/**
 * Formats a number (wei) into a string with the largest possible unit
 * (ETH, GWEI, or WEI) and up to 4 decimal digits.
 *
 * @param weiValue - The numeric value in wei (e.g., from parseInt(tx.value, 16)).
 * @returns The value formatted as a string with the appropriate unit.
 */
export function formatEth(weiValue: number): string {
  // 1 ETH = 1e18 wei
  const WEI_IN_ETH = 1e18;
  // 1 GWEI = 1e9 wei
  const WEI_IN_GWEI = 1e9;

  let result: string;
  if (weiValue == 0) result = "-";
  else if (weiValue >= 100_000_000_000_000) {
    // Convert wei to ETH
    const ethValue = weiValue / WEI_IN_ETH;
    result = `${trimDecimals(ethValue, 4)} ${_gasToken}`;
  } else if (weiValue >= 100_000) {
    // Convert wei to GWEI
    const gweiValue = weiValue / WEI_IN_GWEI;
    result = `${trimDecimals(gweiValue, 4)} gwei`;
  } else {
    // Value is small enough to stay in wei
    result = `${weiValue} wei`;
  }

  return result;
}
