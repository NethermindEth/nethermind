// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.State.Proofs;

namespace Nethermind.Eez.Sequencer;

/// <summary>Produces the blocks of an L1 slot that hold pool transactions: the Live and Future blocks.</summary>
public interface ILiveBlockProducer
{
    /// <returns>The processed block on <paramref name="parent"/>, not yet inserted, or <see langword="null"/> when processing fails.</returns>
    Block? Produce(BlockHeader parent, CancellationToken token);
}

/// <summary>
/// Produces a block from the transaction pool with the header derivation rebuilds: every field
/// <see cref="DerivedHeader.Build"/> decides, an empty extra data and the configured beneficiary, both of which the
/// batch's DA carries. The pool's own rules keep system and blob transactions out, and the production executor
/// leaves out a transaction that does not execute instead of failing the block.
/// </summary>
public sealed class LiveBlockProducer(IBlockProducerEnvFactory envFactory, ISpecProvider specProvider, EezSettlementContext context, Address beneficiary)
    : ILiveBlockProducer
{
    private readonly IBlockProducerEnv _env = envFactory.CreatePersistent();

    public Block? Produce(BlockHeader parent, CancellationToken token)
    {
        IReleaseSpec spec = specProvider.GetSpec(parent.Number + 1, DerivedHeader.Timestamp(parent, context));
        BlockHeader header = DerivedHeader.Build(parent, spec, context, beneficiary, []);
        Withdrawal[]? withdrawals = DerivedHeader.Withdrawals(spec);
        header.WithdrawalsRoot = withdrawals is null ? null : WithdrawalTrie.CalculateRoot(withdrawals);
        header.TotalDifficulty = parent.TotalDifficulty;
        BlockToProduce block = new(header, _env.TxSource.GetTransactions(parent, header, header.GasLimit, null, filterSource: true), [], withdrawals);
        return _env.ChainProcessor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance, token);
    }
}
