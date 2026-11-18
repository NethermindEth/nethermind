// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Receipts;

public interface IReceiptsRootCalculator
{
    Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot);

    /// <summary>Computes the receipts root of a block's receipts as a receipts message carries them, without decoding them.</summary>
    /// <remarks>
    /// The root must be the one <see cref="GetReceiptsRoot(TxReceipt[], IReceiptSpec, Hash256?)"/> gives for the decoded
    /// receipts when they are in canonical form. A root that differs from the expected one is checked again on the
    /// decoded receipts, which that overload may normalize. The default computes nothing, so a chain whose receipts take
    /// another form keeps decoding them.
    /// </remarks>
    /// <param name="blockReceipts">The encoded sequence of the block's receipts.</param>
    /// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
    /// <returns>The root, or <see langword="null"/> when the receipts must be decoded for it.</returns>
    Hash256? GetReceiptsRoot(ReadOnlyMemory<byte> blockReceipts, bool withBloom, IReceiptSpec spec) => null;
}
