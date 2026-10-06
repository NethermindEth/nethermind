// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

internal interface IBlockProcessingProgress
{
    /// <summary>The index of the transaction block processing started last, -1 before the first.</summary>
    int MainThreadTxIndex { get; }
}
