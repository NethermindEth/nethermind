// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Buffers.Binary;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Holds the node's own eth2 <c>MetaData</c>, served over the <c>metadata</c> and <c>ping</c> protocols.</summary>
/// <remarks>
/// The attestation subnet bitfield holds the subnets joined for local aggregators (<see cref="SetAttnets"/>) and is empty at
/// startup; the sync committee bitfield stays empty. The custody group count uses the same configuration as <see cref="Discovery.LocalCustody"/>.
/// </remarks>
/// <param name="config">Local custody settings, or the standard custody requirement when omitted.</param>
/// <param name="store">Optional durable metadata storage.</param>
/// <param name="validateStoreSchema">Validates the full beacon-state schema; disable only for a metadata-only networking store.</param>
public class LocalMetadataSource(IBeaconChainConfig? config = null, BeaconChainStore? store = null, bool validateStoreSchema = true)
{
    private const string SequenceKey = "p2pMetadata";

    private readonly Lock _updateLock = new();

    private readonly Lazy<MetaDataV3> _initial = new(() => new()
    {
        SeqNumber = Sequence(config?.CustodyGroupCount ?? Eip7594DasConstants.CustodyRequirement, store, validateStoreSchema),
        Attnets = new BitArray(64),
        Syncnets = new BitArray(4),
        CustodyGroupCount = config?.CustodyGroupCount ?? Eip7594DasConstants.CustodyRequirement,
    });

    public MetaDataV3 Current
    {
        get => field ?? _initial.Value;
        set;
    }

    /// <summary>Advertises <paramref name="attnets"/> under the next sequence number.</summary>
    /// <remarks>
    /// phase0/p2p-interface.md MetaData: a change to any field increments <c>seq_number</c>. The sequence a restart starts from is
    /// persisted first, one past the new one, because a restarted node advertises no subnet under it.
    /// </remarks>
    internal void SetAttnets(BitArray attnets)
    {
        lock (_updateLock)
        {
            MetaDataV3 current = Current;
            ulong sequence = current.SeqNumber + 1;
            if (store is not null) Persist(store, sequence + 1, current.CustodyGroupCount);
            Current = new MetaDataV3 { SeqNumber = sequence, Attnets = attnets, Syncnets = current.Syncnets, CustodyGroupCount = current.CustodyGroupCount };
        }
    }

    private static ulong Sequence(ulong count, BeaconChainStore? store, bool validateStoreSchema)
    {
        if (store is null) return 1;
        if (validateStoreSchema) store.EnsureSchemaVersion();
        // Previously every configuration advertised sequence 1, so the first persisted version supersedes it.
        ulong sequence = 2;
        if (store.GetMetadata(SequenceKey) is { Length: 2 * sizeof(ulong) } previous)
        {
            sequence = BinaryPrimitives.ReadUInt64BigEndian(previous);
            if (BinaryPrimitives.ReadUInt64BigEndian(previous.AsSpan(sizeof(ulong))) != count) sequence = checked(sequence + 1);
        }
        Persist(store, sequence, count);
        return sequence;
    }

    private static void Persist(BeaconChainStore store, ulong sequence, ulong count)
    {
        byte[] value = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, sequence);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(sizeof(ulong)), count);
        store.PutMetadata(SequenceKey, value);
    }
}
