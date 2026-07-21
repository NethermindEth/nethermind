// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    {
        error = null;

        List<FrameDependency> deps = [];
        foreach (Transaction tx in focil.Transactions)
        {
            deps.AddRange(Eip8288Dependencies.ForTransaction(tx));
        }

        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
        if (focil.RecursiveStark.BlockDepsHash.ValueHash256 != depsHash)
        {
            error = DepsHashMismatch;
            return false;
        }

        if (!verifier.VerifyRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, focil.RecursiveStark.StarkProof))
        {
            error = InvalidProof;
            return false;
        }

        return true;
    }
}
