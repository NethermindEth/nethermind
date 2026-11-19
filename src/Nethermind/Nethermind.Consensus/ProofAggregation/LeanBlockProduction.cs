// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>The EIP-8288 state of one block being produced: its proof inputs, the witnesses they cover and its limits.</summary>
/// <remarks>
/// A candidate's contribution is held as pending until the executor's verdict on it is final, which is the
/// <see cref="BlockProcessor.AddingTxEventArgs.Action"/> of the args it was picked with: the executor sets <c>Skip</c> there
/// when the transaction fails or is rolled back. It is committed on the next pick or the next read of <see cref="ProofInputs"/>.
/// </remarks>
internal sealed class LeanBlockProduction
{
    private readonly List<AggregationInput> _proofInputs = [];
    private Pending? _pending;

    public LeanProofBudget Budget { get; private init; } = new();

    /// <summary>The verified proof input of the block's inclusion lists, if any.</summary>
    public AggregationInput? InclusionListProofInput { get; set; }

    /// <summary>When set, only transactions whose dependencies all lie in this proven set are picked.</summary>
    public IReadOnlySet<FrameDependency>? DependencyLimit { get; set; }

    /// <summary>The proof inputs of the transactions included so far.</summary>
    public List<AggregationInput> ProofInputs
    {
        get
        {
            Settle();
            return _proofInputs;
        }
    }

    /// <summary>Records the contribution of a picked candidate, to be committed once it is included.</summary>
    public void Prepare(BlockProcessor.AddingTxEventArgs args, AggregationInput contribution, IReadOnlyList<FrameDependency> required)
    {
        Settle();
        _pending = new(args, contribution, required);
    }

    public bool IsPrepared(BlockProcessor.AddingTxEventArgs args) => ReferenceEquals(_pending?.Args, args);

    /// <summary>Commits the pending contribution if its transaction was included.</summary>
    public void Settle()
    {
        if (_pending is not { } pending) return;
        _pending = null;
        if (pending.Args.Action != BlockProcessor.TxAction.Add) return;
        AggregationInput contribution = pending.Contribution;
        if (contribution.Deps.Count != 0 || contribution.RecursiveProofs.Count != 0) _proofInputs.Add(contribution);
        Budget.Commit(contribution, pending.Required);
    }

    public LeanBlockProduction Clone()
    {
        Settle();
        LeanBlockProduction clone = new()
        {
            Budget = Budget.Clone(),
            InclusionListProofInput = InclusionListProofInput,
            DependencyLimit = DependencyLimit
        };
        clone._proofInputs.AddRange(_proofInputs);
        return clone;
    }

    private sealed record Pending(BlockProcessor.AddingTxEventArgs Args, AggregationInput Contribution, IReadOnlyList<FrameDependency> Required);
}
