// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Adds the EIP-8288 checks to whichever block production transaction picker is registered.</summary>
/// <remarks>
/// The checks run when the inner picker raises <see cref="BlockProcessor.IBlockProductionTransactionPicker.AddingTransaction"/>,
/// so a candidate they skip is not raised to this picker's subscribers, and again on the returned args for a picker that
/// adds without raising it. A <c>Stop</c> from the inner picker comes from checks that precede the transaction size check
/// and stands.
/// </remarks>
internal sealed class Eip8288TransactionPicker : BlockProcessor.IBlockProductionTransactionPicker
{
    // len(rlp(stark_proof)) - 1: the one byte of an empty stark_proof is already part of the block's fixed overhead.
    private static readonly int MaxDependencyProofLength = Rlp.LengthOfByteString(Eip8288Constants.MaxAggregateProofBytes, 0) - 1;

    private readonly BlockProcessor.IBlockProductionTransactionPicker _inner;
    private readonly ISpecProvider _specProvider;
    private readonly LeanProofStore _leanProofStore;
    private readonly long _maxTxLengthBytes;

    public Eip8288TransactionPicker(BlockProcessor.IBlockProductionTransactionPicker inner, ISpecProvider specProvider,
        IBlocksConfig blocksConfig, LeanProofStore leanProofStore)
    {
        _inner = inner;
        _specProvider = specProvider;
        _leanProofStore = leanProofStore;
        _maxTxLengthBytes = blocksConfig.BlockProductionMaxTxKilobytes.KiB;
        inner.AddingTransaction += OnInnerAddingTransaction;
    }

    public event EventHandler<BlockProcessor.AddingTxEventArgs>? AddingTransaction;

    public BlockProcessor.AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx,
        IReadOnlySet<Transaction> transactionsInBlock, IReadOnlyStateProvider stateProvider,
        ulong cumulativeBlockExecutionGas, ulong cumulativeBlockStateGas)
    {
        BlockProcessor.AddingTxEventArgs args = _inner.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider,
            cumulativeBlockExecutionGas, cumulativeBlockStateGas);
        if (args.Action != BlockProcessor.TxAction.Stop && !(block is BlockToProduce producing && producing.LeanProduction.IsChecked(args)))
            Check(args);
        return args;
    }

    private void OnInnerAddingTransaction(object? sender, BlockProcessor.AddingTxEventArgs args)
    {
        if (Check(args)) AddingTransaction?.Invoke(this, args);
    }

    /// <summary>Applies the checks to the inner picker's verdict on a candidate.</summary>
    /// <returns><c>true</c> when the candidate is still added.</returns>
    private bool Check(BlockProcessor.AddingTxEventArgs args)
    {
        Block block = args.Block;
        Transaction currentTx = args.Transaction;
        BlockToProduce? producing = block as BlockToProduce;
        producing?.LeanProduction.MarkChecked(args);
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        if (!spec.IsEip8288Enabled) return args.Action == BlockProcessor.TxAction.Add;

        if (producing is not null && producing.TxByteLength + currentTx.GetLength(false) + DependencyProofReserve(producing, currentTx) > _maxTxLengthBytes)
        {
            // If smallest tx is too large, stop picking
            args.Set(currentTx.GasLimit == GasCostOf.Transaction ? BlockProcessor.TxAction.Stop : BlockProcessor.TxAction.Skip, "Too large for CL");
            return false;
        }

        if (args.Action != BlockProcessor.TxAction.Add) return false;
        if (Eip8288Dependencies.RecursiveStarkGas(currentTx) == 0) return true;

        List<FrameDependency> required = Eip8288Dependencies.ForTransaction(currentTx, FrameTxSigHash.ComputeValue);
        LeanBlockProduction? production = producing?.LeanProduction;
        if (production?.DependencyLimit is { } limit)
            foreach (FrameDependency dependency in required)
                if (!limit.Contains(dependency)) return Skip(args, "Dependency proof not ready");
        LeanProofBudget budget = production?.Budget ?? new();
        List<FrameDependency> missing = budget.Missing(required);
        AggregationInput candidateInput = new();
        if (missing.Count != 0 && !budget.TryUseInclusionList(production?.InclusionListProofInput, missing, out candidateInput)
            && !_leanProofStore.TryGetInput(missing, out candidateInput))
            return Skip(args, "Missing verified dependency witnesses");
        if (!budget.TryPrepare(candidateInput, required, out AggregationInput contribution, out string? proofError))
            return Skip(args, proofError!);
        production?.Prepare(args, contribution, required);
        return true;
    }

    private static bool Skip(BlockProcessor.AddingTxEventArgs args, string reason)
    {
        args.Set(BlockProcessor.TxAction.Skip, reason);
        return false;
    }

    /// <summary>The body bytes kept for the EIP-8288 <c>stark_proof</c> once the block carries dependencies.</summary>
    /// <remarks>The proof counts toward the EIP-7934 RLP block size and the CL payload but is attached only after packing,
    /// so the largest admissible proof, the local <see cref="Eip8288Constants.MaxAggregateProofBytes"/> bound, is reserved
    /// out of the transaction byte budget, measured against an empty <c>stark_proof</c> in place.</remarks>
    private static int DependencyProofReserve(BlockToProduce block, Transaction candidate) =>
        block.LeanProduction.ProofInputs.Count != 0 || Eip8288Dependencies.IsDependencyBearing(candidate)
            ? MaxDependencyProofLength
            : 0;
}
