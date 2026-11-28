// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

/// <summary>Gives a block read back without its EIP-8141 dependency proof the proof stored for it, so its claims can be
/// checked.</summary>
/// <remarks>Blocks are cached and read back without their proofs, which are kept apart from the bodies.</remarks>
public sealed class StoredDependencyProofBlockProcessor(IBlockProcessor inner, IDependencyProofStore proofStore) : IBlockProcessor
{
    public event Action<Block>? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer,
        IReleaseSpec spec, CancellationToken token = default) =>
        inner.ProcessOne(spec.IsEip8141Enabled ? proofStore.WithProof(suggestedBlock) : suggestedBlock, options, blockTracer, spec, token);
}
