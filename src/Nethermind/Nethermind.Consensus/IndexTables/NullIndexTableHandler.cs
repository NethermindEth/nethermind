// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.IndexTables;

public class NullIndexTableHandler : IIndexTableHandler
{
    public void CommitIndexTableRoots(Block block, TxReceipt[] receipts, IReleaseSpec spec, ITxTracer tracer) { }
    public void RollbackBlock(Block block) { }
    public void UpdateFinalBlockHash(Block block) { }

    public static IIndexTableHandler Instance { get; } = new NullIndexTableHandler();
}
