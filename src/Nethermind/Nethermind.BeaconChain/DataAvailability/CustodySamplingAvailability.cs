// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The production <c>is_data_available</c> for a node that custodies a fraction of the matrix: the
/// block is available once this node holds a verified sidecar for every column it custodies AND
/// custody sampling succeeded, i.e. every column of its per-slot sample is held and verified
/// (das-core.md: "Sampling is considered successful if the node manages to retrieve all selected
/// columns"). The sample already contains the custody columns by construction; both are still
/// checked by name so the rule does not silently rest on that derivation staying true. Blocks
/// older than the <see cref="DataAvailabilityBoundary"/> are available without any column: the
/// network no longer guarantees to serve them (fork-choice.md <c>is_data_available</c>). A pre-Fulu block
/// with blobs is never available inside the blob sidecar retention window: deneb/fork-choice.md
/// <c>is_data_available</c> needs its blob sidecars, and this node has no blob sidecar source.
/// </summary>
/// <remarks>
/// Sidecars are bound to the block on every check; successful cryptographic verification is cached
/// for each immutable sidecar instance. Fails closed while the
/// node's identity is unknown (<see cref="INodeColumnCustodySource.Current"/> is <c>null</c>), since a
/// rule that cannot say which columns it needs cannot say a block is available either. The window
/// is read from <paramref name="clock"/> at every check because it moves with the wall clock.
/// </remarks>
public sealed class CustodySamplingAvailability(INodeColumnCustodySource custodySource, IDataColumnSource columns, SlotClock clock) : IDataAvailabilityRule
{
    private readonly ConditionalWeakTable<DataColumnSidecar, object> _verified = [];

    /// <inheritdoc/>
    public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec)
    {
        SszKzgCommitment[] blobCommitments = block.Body?.BlobKzgCommitments ?? [];
        if (blobCommitments.Length == 0) return true;

        ulong blockEpoch = spec.GetEpoch(block.Slot);
        if (blockEpoch < spec.FuluForkEpoch) return !DataAvailabilityBoundary.IsInBlobSidecarWindow(blockEpoch, clock.CurrentEpoch);

        if (blockEpoch < DataAvailabilityBoundary.Compute(clock.CurrentEpoch, spec)) return true;

        if (custodySource.Current is not { } custody) return false;

        Dictionary<ulong, DataColumnSidecar> held = [];
        bool Hold(ulong column)
        {
            if (held.ContainsKey(column)) return true;
            if (!columns.TryGetColumn(blockRoot, column, out DataColumnSidecar? sidecar)) return false;
            held[column] = sidecar;
            return true;
        }

        foreach (ulong column in custody.CustodyColumns)
            if (!Hold(column)) return false;
        foreach (ulong column in custody.SampledColumns)
            if (!Hold(column)) return false;

        foreach ((ulong column, DataColumnSidecar sidecar) in held)
        {
            if (sidecar.Index != column || !DataColumnAvailability.MatchesBlock(sidecar, blockRoot, blobCommitments)) return false;
            if (!_verified.TryGetValue(sidecar, out _))
            {
                if (!DataColumnSidecarVerifier.Verify(sidecar, spec)) return false;
                _verified.GetOrCreateValue(sidecar);
            }
        }

        return true;
    }
}
