// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Crypto;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

/// <summary>
/// EIP-8288 FOCIL inclusion list extended with a recursive STARK: <c>[transactions, recursive_stark]</c>.
/// Self-contained — it carries both the transactions and a proof that all their dependencies are valid,
/// analogous to how a block carries a recursive STARK (spec "FOCIL Compatibility").
/// </summary>
public sealed class FocilInclusionList
{
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required RecursiveStark RecursiveStark { get; init; }
}

/// <summary>Validates that a FOCIL's recursive STARK proves the dependencies of all its transactions.</summary>
public static class FocilInclusionListValidator
{
    public const string DepsHashMismatch = "FOCIL recursive STARK public input must equal hash(deps)";
    public const string InvalidProof = "FOCIL recursive STARK failed verification";

    public static bool Validate(FocilInclusionList focil, ILeanProofVerifier verifier, out string? error)
        => Validate(focil.Transactions, focil.RecursiveStark, verifier, out _, out error);

    public static bool Validate(IReadOnlyList<Transaction> transactions, RecursiveStark? proof,
        ILeanProofVerifier verifier, out List<FrameDependency> deps, out string? error)
    {
        error = null;
        deps = [];
        try
        {
            foreach (Transaction tx in transactions)
            {
                if (tx is null) { error = InvalidProof; return false; }
                deps.AddRange(Eip8288Dependencies.ForTransaction(tx));
            }
            deps = Eip8288Dependencies.Canonicalize(deps);
        }
        catch (ArgumentException)
        {
            error = InvalidProof;
            return false;
        }

        // Ordinary lists keep the EIP-7805 wire shape when no dependencies need proving.
        if (proof is null && deps.Count == 0) return true;
        if (proof?.BlockDepsHash is null || proof.StarkProof is not { Length: > 0 and <= Eip8288Constants.MaxProofBytes })
        {
            error = InvalidProof;
            return false;
        }
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
        if (proof.BlockDepsHash.ValueHash256 != depsHash)
        {
            error = DepsHashMismatch;
            return false;
        }
        if (!verifier.VerifyRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, proof.StarkProof))
        {
            error = InvalidProof;
            return false;
        }
        return true;
    }
}
