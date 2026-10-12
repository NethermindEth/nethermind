// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts;

// Lazy, so the guest runs no class constructor for this type.
public partial class ReceiptsRootCalculator
{
    private static IRlpDecoder<TxReceipt> _decoder => field ??= Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie);
    private static ReceiptMessageDecoder _skipStateDecoder => field ??= new(skipStateAndStatus: true);
}
