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
/// The node does not attest, so the attestation subnet bitfields stay empty. The custody group count
/// uses the same configuration as <see cref="Discovery.LocalCustody"/>.
/// </remarks>
public class LocalMetadataSource(IBeaconChainConfig? config = null, BeaconChainStore? store = null)
{
    public MetaDataV3 Current { get; set; } = new()
    {
        SeqNumber = Sequence(config?.CustodyGroupCount ?? Eip7594DasConstants.CustodyRequirement, store),
        Attnets = new BitArray(64),
        Syncnets = new BitArray(4),
        CustodyGroupCount = config?.CustodyGroupCount ?? Eip7594DasConstants.CustodyRequirement,
    };

    private static ulong Sequence(ulong count, BeaconChainStore? store)
    {
        if (store is null) return 1;
        const string key = "p2pMetadata";
        // Previously every configuration advertised sequence 1, so the first persisted version supersedes it.
        ulong sequence = 2;
        if (store.GetMetadata(key) is { Length: 2 * sizeof(ulong) } previous)
        {
            sequence = BinaryPrimitives.ReadUInt64BigEndian(previous);
            if (BinaryPrimitives.ReadUInt64BigEndian(previous.AsSpan(sizeof(ulong))) != count) sequence = checked(sequence + 1);
        }
        byte[] value = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, sequence);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(sizeof(ulong)), count);
        store.PutMetadata(key, value);
        return sequence;
    }
}
