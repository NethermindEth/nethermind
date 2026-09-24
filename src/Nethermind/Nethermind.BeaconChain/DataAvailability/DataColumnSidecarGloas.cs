// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Gloas <c>DataColumnSidecar</c> (specs/gloas/p2p-interface.md). Field order and types match the spec
/// exactly; a mismatch here changes the hash tree root.
/// </summary>
/// <remarks>
/// EIP-7732 removes the Fulu commitments, signed block header and inclusion proof (see
/// <see cref="DataColumnSidecar"/>) and adds <see cref="Slot"/> and <see cref="BeaconBlockRoot"/>;
/// EIP-7688 makes <see cref="Column"/> and <see cref="KzgProofs"/> progressive lists.
/// </remarks>
[SszContainer]
public partial class DataColumnSidecarGloas
{
    /// <summary>The column index in the extended data matrix, in <c>[0, NUMBER_OF_COLUMNS)</c>.</summary>
    public ulong Index { get; set; }

    /// <summary>One cell per blob in the block, at <see cref="Index"/>'s column position.</summary>
    [SszProgressiveList]
    public SszBlobCell[]? Column { get; set; }

    /// <summary>One cell KZG proof per blob, for this column. <c>KZGProof</c> is a 48-byte ByteVector,
    /// so it reuses <see cref="SszKzgCommitment"/>'s wire/merkleization shape.</summary>
    [SszProgressiveList]
    public SszKzgCommitment[]? KzgProofs { get; set; }

    public ulong Slot { get; set; }

    public Hash256? BeaconBlockRoot { get; set; }
}
