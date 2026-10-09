// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.IndexTables;

/// <summary>Handler for environments without EIP-8304 index tables; every operation is a no-op.</summary>
public class NullIndexTableHandler : IIndexTableHandler
{
    /// <inheritdoc />
    public void CommitIndexTableRoots(Block block, TxReceipt[] receipts, IReleaseSpec spec, ITxTracer tracer) { }
    /// <inheritdoc />
    public void RollbackBlock(Block block) { }
    /// <inheritdoc />
    public void UpdateFinalBlockHash(Block block) { }

    public static IIndexTableHandler Instance { get; } = new NullIndexTableHandler();
}
