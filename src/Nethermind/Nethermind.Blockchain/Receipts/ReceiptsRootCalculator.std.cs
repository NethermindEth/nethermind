// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts;

public partial class ReceiptsRootCalculator
{
    private static readonly IRlpDecoder<TxReceipt> _decoder = Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie);
    private static readonly ReceiptMessageDecoder _skipStateDecoder = new(skipStateAndStatus: true);
}
