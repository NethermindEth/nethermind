// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Test.DataAvailability;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Real KZG blobs for Gloas sidecar tests: the commitments a bid would carry, and a valid
/// <see cref="DataColumnSidecarGloas"/> for any column of them. The cells and proofs come from
/// <see cref="DataColumnKzgFixture"/>, never from the verifier under test.
/// </summary>
internal static class DataColumnSidecarGloasTestFixture
{
    private static readonly Lazy<DataColumnKzgFixture.BlobFixture[]> Blobs = new(() =>
        [DataColumnKzgFixture.BuildBlob(0x10), DataColumnKzgFixture.BuildBlob(0x20)]);

    public static Hash256 BlockRoot { get; } = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());

    public static SszKzgCommitment[] Commitments() => [.. Blobs.Value.Select(DataColumnKzgFixture.CommitmentOf)];

    public static DataColumnSidecarGloas BuildSidecar(ulong column, ulong slot = 1, Hash256? blockRoot = null) => new()
    {
        Index = column,
        Column = [.. Blobs.Value.Select(b => DataColumnKzgFixture.CellAt(b, (int)column))],
        KzgProofs = [.. Blobs.Value.Select(b => DataColumnKzgFixture.ProofAt(b, (int)column))],
        Slot = slot,
        BeaconBlockRoot = blockRoot ?? BlockRoot,
    };
}
