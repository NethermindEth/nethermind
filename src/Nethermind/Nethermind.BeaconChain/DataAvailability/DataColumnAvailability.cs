// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The per-sidecar predicates every <see cref="IDataAvailabilityRule"/> is built from. Split out so
/// each cross-check is provably exercised on its own, independent of real KZG cryptography.
/// </summary>
public static class DataColumnAvailability
{
    /// <summary>
    /// Whether <paramref name="sidecar"/> counts towards <paramref name="blockRoot"/>'s availability: it is
    /// addressed to that block (<see cref="MatchesBlock"/>) and independently verifies (structure, blob
    /// count, inclusion proof, KZG proofs). Both halves are required to fail closed: a sidecar that
    /// verifies against its OWN header proves nothing about this block, and one addressed to this block
    /// proves nothing until its proofs check out.
    /// </summary>
    public static bool IsVerifiedColumnOf(DataColumnSidecar sidecar, Hash256 blockRoot, IReadOnlyList<SszKzgCommitment> blobCommitments, BeaconChainSpec spec) =>
        MatchesBlock(sidecar, blockRoot, blobCommitments) && DataColumnSidecarVerifier.Verify(sidecar, spec);

    /// <summary>
    /// Whether <paramref name="dataColumns"/> covers every column index in
    /// <c>[0, NumberOfColumns)</c> exactly once - no gaps (too few sidecars, or none at all), no
    /// duplicates (many sidecars hoarding one index while another goes unfilled), no index outside
    /// the valid range.
    /// </summary>
    public static bool HasExactlyOneSidecarPerColumn(IReadOnlyList<DataColumnSidecar>? dataColumns)
    {
        if (dataColumns is null || dataColumns.Count != Eip7594DasConstants.NumberOfColumns) return false;

        bool[] seen = new bool[Eip7594DasConstants.NumberOfColumns];
        foreach (DataColumnSidecar sidecar in dataColumns)
        {
            if (sidecar.Index >= (ulong)Eip7594DasConstants.NumberOfColumns || seen[sidecar.Index]) return false;
            seen[sidecar.Index] = true;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="sidecar"/> is even addressed to this block: its header round-trips to
    /// <paramref name="blockRoot"/>, and it claims exactly <paramref name="blobCommitments"/> - no more, no
    /// fewer, no substitutions. Required but not sufficient: a sidecar can pass this and still fail its
    /// own KZG or inclusion proof, which <see cref="IsVerifiedColumnOf"/> checks separately.
    /// </summary>
    public static bool MatchesBlock(DataColumnSidecar sidecar, Hash256 blockRoot, IReadOnlyList<SszKzgCommitment> blobCommitments)
    {
        if (sidecar.SignedBlockHeader?.Message is not { } header || SszRoots.HashTreeRoot(header) != blockRoot)
            return false;

        if (sidecar.KzgCommitments is not { } commitments || commitments.Length != blobCommitments.Count)
            return false;

        for (int i = 0; i < commitments.Length; i++)
        {
            if (!commitments[i].AsSpan().SequenceEqual(blobCommitments[i].AsSpan())) return false;
        }

        return true;
    }
}
