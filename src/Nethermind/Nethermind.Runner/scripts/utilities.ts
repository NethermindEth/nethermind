

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
 * This function mimics the C# code: 
 * - it only processes if bytes.length <= 32,
 * - decodes UTF-8 codepoints individually,
 * - skips control characters (potentially inserting a single space if they've appeared after valid text),
 * - stops on incomplete sequences, 
 * - and returns the final string.
 */
function toCleanUtf8String(bytes: Uint8Array): string {
  // The C# code short-circuits if empty or more than 32 bytes
  if (bytes.length === 0 || bytes.length > 32) {
    return "";
  }

  let resultChars: string[] = [];
  let index = 0;
  let hasValidContent = false;
  let shouldAddSpace = false;

  while (index < bytes.length) {
    // Decode next code point from UTF-8
    const { codePoint, consumed, status } = decodeUtf8Rune(bytes, index);

    if (status === "done") {
      // Check if codePoint is a control character
      if (!isControlCharacter(codePoint)) {
        // If we previously skipped a control char and wanted to insert a space, do so now
        if (shouldAddSpace) {
          resultChars.push(" ");
          shouldAddSpace = false;
        }
        // Add the decoded character
        resultChars.push(String.fromCodePoint(codePoint));
        hasValidContent = true;
      } else {
        // We encountered a control char, we mark that maybe we should insert
        // a space if we have had valid content
        shouldAddSpace = shouldAddSpace || hasValidContent;
      }
      index += consumed;
    } else if (status === "needMoreData") {
      // Incomplete sequence at the end => break
      break;
    } else {
      // status === "invalid"
      // Skip invalid byte, but set up for possible space
      shouldAddSpace = shouldAddSpace || hasValidContent;
      index += 1;
    }
  }

  return resultChars.join("");
}

/**
 * Attempts to decode one UTF-8 code point from `bytes` starting at `offset`.
 * 
 * Returns an object:
 *   - codePoint: number (the decoded Unicode code point if successful)
 *   - consumed: number (how many bytes were consumed)
 *   - status: "done" | "needMoreData" | "invalid"
 */
function decodeUtf8Rune(bytes: Uint8Array, offset: number): {
  codePoint: number;
  consumed: number;
  status: "done" | "needMoreData" | "invalid";
} {
  if (offset >= bytes.length) {
    return { codePoint: 0, consumed: 0, status: "needMoreData" };
  }

  const b0 = bytes[offset];

  // 1-byte sequence
  if ((b0 & 0x80) === 0) {
    // ASCII
    return { codePoint: b0, consumed: 1, status: "done" };
  }

  // For multi-byte, ensure we have enough data
  // and check validity according to UTF-8 rules
  if ((b0 & 0xe0) === 0xc0) {
    // 2-byte sequence: 110xxxxx 10xxxxxx
    if (offset + 1 >= bytes.length) {
      return { codePoint: 0, consumed: 0, status: "needMoreData" };
    }
    const b1 = bytes[offset + 1];
    if ((b1 & 0xc0) !== 0x80 || (b0 & 0xfe) === 0xc0) {
      // Overlong or invalid continuation
      return { codePoint: 0, consumed: 0, status: "invalid" };
    }
    const codePoint = ((b0 & 0x1f) << 6) | (b1 & 0x3f);
    return { codePoint, consumed: 2, status: "done" };
  }

  if ((b0 & 0xf0) === 0xe0) {
    // 3-byte sequence: 1110xxxx 10xxxxxx 10xxxxxx
    if (offset + 2 >= bytes.length) {
      return { codePoint: 0, consumed: 0, status: "needMoreData" };
    }
    const b1 = bytes[offset + 1];
    const b2 = bytes[offset + 2];
    if (((b1 & 0xc0) !== 0x80) || ((b2 & 0xc0) !== 0x80)) {
      return { codePoint: 0, consumed: 0, status: "invalid" };
    }
    // Check for overlong forms
    const cp = ((b0 & 0x0f) << 12) | ((b1 & 0x3f) << 6) | (b2 & 0x3f);
    // Overlong or invalid
    if ((cp >= 0xd800 && cp <= 0xdfff) || cp === 0) {
      return { codePoint: 0, consumed: 0, status: "invalid" };
    }
    return { codePoint: cp, consumed: 3, status: "done" };
  }

  if ((b0 & 0xf8) === 0xf0) {
    // 4-byte sequence: 11110xxx 10xxxxxx 10xxxxxx 10xxxxxx
    if (offset + 3 >= bytes.length) {
      return { codePoint: 0, consumed: 0, status: "needMoreData" };
    }
    const b1 = bytes[offset + 1];
    const b2 = bytes[offset + 2];
    const b3 = bytes[offset + 3];
    if (((b1 & 0xc0) !== 0x80) ||
      ((b2 & 0xc0) !== 0x80) ||
      ((b3 & 0xc0) !== 0x80)) {
      return { codePoint: 0, consumed: 0, status: "invalid" };
    }
    const cp = ((b0 & 0x07) << 18) | ((b1 & 0x3f) << 12) | ((b2 & 0x3f) << 6) | (b3 & 0x3f);
    // Check for valid range (UTF-8 up to U+10FFFF)
    if (cp > 0x10ffff || cp < 0x10000) {
      // Could be overlong or out of range
      return { codePoint: 0, consumed: 0, status: "invalid" };
    }
    return { codePoint: cp, consumed: 4, status: "done" };
  }

  // If we reach here, it's invalid
  return { codePoint: 0, consumed: 0, status: "invalid" };
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
  "1": "Ethereum mainnet",
  "480": "World Mainnet",
  "8453": "Base mainnet"
}
const logos = {
  "Mainnet": "ethereum-logo.svg",
  "1": "ethereum-logo.svg",
  "480": "world-logo.svg",
  "8453": "base-logo.svg"
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
