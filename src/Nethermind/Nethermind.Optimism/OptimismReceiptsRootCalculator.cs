// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Optimism;

/// <summary>Computes Optimism receipts roots from decoded receipts only.</summary>
/// <remarks>
/// <see cref="ReceiptsRootCalculator"/> encodes decoded receipts with the receipt codec registered for the trie, which
/// is Optimism's here. Receipts as a receipts message carries them are not read in Optimism's form, so it computes no
/// root for those and they are decoded for it.
/// </remarks>
public sealed class OptimismReceiptsRootCalculator : IReceiptsRootCalculator
{
    public Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot) =>
        ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, spec, suggestedRoot);
}
