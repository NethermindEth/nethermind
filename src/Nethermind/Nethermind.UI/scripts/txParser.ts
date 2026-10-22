// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

export function getTxType(type: number): string {
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
export function getMethod(type: string): string {
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
export function getToAddress(to: string): string {
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
