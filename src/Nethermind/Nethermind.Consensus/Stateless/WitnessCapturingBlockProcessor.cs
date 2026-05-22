// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// <see cref="IBlockProcessor"/> decorator that arms <see cref="WitnessCapturingWorldStateProxy"/>
/// before each <see cref="ProcessOne"/> and drains it on success. Confines the witness-capture
/// lifecycle to <c>ProcessOne</c>'s scope so <see cref="BranchProcessor"/> doesn't need to know
/// about witnesses at all.
/// </summary>
public sealed class WitnessCapturingBlockProcessor(
    IBlockProcessor inner,
    WitnessCapturingWorldStateProxy proxy) : IBlockProcessor
{
    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(
        Block suggestedBlock,
        ProcessingOptions options,
        IBlockTracer blockTracer,
        IReleaseSpec spec,
        CancellationToken token = default)
    {
        using WitnessCaptureSession session = proxy.BeginCapture(
            suggestedBlock.Hash,
            suggestedBlock.ParentHash,
            suggestedBlock.Number,
            options);

        (Block Block, TxReceipt[] Receipts) result = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        session.Drain();
        return result;
    }
}
