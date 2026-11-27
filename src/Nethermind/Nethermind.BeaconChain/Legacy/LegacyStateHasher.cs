// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.BeaconChain.Legacy.Phase0;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.Legacy;

/// <summary>Computes the state root a fork before Fulu would compute over a <see cref="BeaconStateFulu"/> working state.</summary>
/// <remarks>
/// The fields of every such fork are a prefix of Fulu's, except for the Phase0 attestation lists and the Bellatrix and
/// Capella payload headers, so the current hasher's field roots are reused and only those leaves are replaced.
/// </remarks>
internal sealed class LegacyStateHasher(LegacyFork fork) : IBeaconStateHasher
{
    private const int PreviousEpochField = 15;
    private const int CurrentEpochField = 16;
    private const int ExecutionPayloadHeaderField = 24;
    private static readonly int s_pendingAttestationsDepth = BitOperations.Log2((uint)LegacyPresets.MaxPendingAttestations);

    private readonly CachedBeaconStateHasher _hasher = new();
    private readonly CachedBeaconStateHasher.ContainerListCache<PendingAttestation> _previousAttestations = new(new CachedBeaconStateHasher.BoundedChunkTree(s_pendingAttestationsDepth));
    private readonly CachedBeaconStateHasher.ContainerListCache<PendingAttestation> _currentAttestations = new(new CachedBeaconStateHasher.BoundedChunkTree(s_pendingAttestationsDepth));
    private readonly CachedBeaconStateHasher.ContainerMemo<Bellatrix.ExecutionPayloadHeader> _bellatrixHeader = new();
    private readonly CachedBeaconStateHasher.ContainerMemo<Capella.ExecutionPayloadHeader> _capellaHeader = new();
    private Hash256? _postBlockRoot;

    /// <summary>The fork whose state shape is hashed; an upgrade advances it.</summary>
    public LegacyFork Fork { get; set; } = fork;

    /// <summary>Keeps the verified root of a block's post-state for the next <c>process_slot</c>, which hashes the same state.</summary>
    public void RecordPostBlockRoot(Hash256 root) => _postBlockRoot = root;

    /// <inheritdoc/>
    public Hash256 HashTreeRoot(BeaconStateFulu state)
    {
        if (_postBlockRoot is { } recorded)
        {
            _postBlockRoot = null;
            return recorded;
        }

        _hasher.HashTreeRoot(state);
        Span<UInt256> fields = stackalloc UInt256[FieldCount(Fork)];
        _hasher.FieldRoots[..fields.Length].CopyTo(fields);
        switch (Fork)
        {
            case LegacyFork.Phase0:
                BeaconState phase0 = (BeaconState)state;
                fields[PreviousEpochField] = _previousAttestations.Root(phase0.PreviousEpochParticipation);
                fields[CurrentEpochField] = _currentAttestations.Root(phase0.CurrentEpochParticipation);
                break;
            case LegacyFork.Bellatrix:
                fields[ExecutionPayloadHeaderField] = _bellatrixHeader.Root((Bellatrix.ExecutionPayloadHeader)state.LatestExecutionPayloadHeader!);
                break;
            case LegacyFork.Capella:
                fields[ExecutionPayloadHeaderField] = _capellaHeader.Root((Capella.ExecutionPayloadHeader)state.LatestExecutionPayloadHeader!);
                break;
        }

        Merkle.Merkleize(out UInt256 root, fields);
        return new Hash256(root.ToLittleEndian());
    }

    private static int FieldCount(LegacyFork fork) => fork switch
    {
        LegacyFork.Phase0 => 21,
        LegacyFork.Altair => 24,
        LegacyFork.Bellatrix => 25,
        LegacyFork.Capella or LegacyFork.Deneb => 28,
        _ => 37,
    };
}
