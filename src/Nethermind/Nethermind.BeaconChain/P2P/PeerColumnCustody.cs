// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Libp2pPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.BeaconChain.P2P;

/// <summary>The data columns a remote peer custodies, which are the only columns it serves over req/resp (fulu/p2p-interface.md).</summary>
public sealed class PeerColumnCustody
{
    private readonly bool[] _columns = new bool[Eip7594DasConstants.NumberOfColumns];

    /// <summary>A peer whose node id is unknown: it is asked for no column.</summary>
    public static PeerColumnCustody None { get; } = new([], isAdvertised: false);

    internal PeerColumnCustody(IEnumerable<ulong> columns, bool isAdvertised)
    {
        foreach (ulong column in columns)
        {
            _columns[column] = true;
        }

        IsAdvertised = isAdvertised;
    }

    /// <summary>Whether the set derives from the custody group count the peer advertised, not from the <c>CUSTODY_REQUIREMENT</c> floor.</summary>
    public bool IsAdvertised { get; }

    public bool Custodies(ulong column) => column < (ulong)_columns.Length && _columns[column];

    /// <summary>How many of <paramref name="columns"/> this peer custodies.</summary>
    internal int CountCustodied(IReadOnlyList<ulong> columns)
    {
        int count = 0;
        foreach (ulong column in columns)
        {
            if (Custodies(column))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The columns of <c>get_custody_groups(node_id, custody_group_count)</c> (fulu/das-core.md).</summary>
    /// <param name="nodeId">The peer's raw discv5 node id.</param>
    /// <param name="custodyGroupCount">The count the peer advertised; <c>null</c> or out of range when unknown.</param>
    /// <remarks>
    /// An unknown count yields the <c>CUSTODY_REQUIREMENT</c> groups: <c>get_custody_groups</c> extends one fixed walk,
    /// so that set is a subset of what any honest peer custodies, and no column outside it is assumed.
    /// </remarks>
    public static PeerColumnCustody ForNode(Hash256 nodeId, ulong? custodyGroupCount)
    {
        bool advertised = custodyGroupCount <= Eip7594DasConstants.NumberOfCustodyGroups;
        List<ulong> columns = [];
        foreach (ulong group in CustodyGroups.GetCustodyGroups(nodeId, advertised ? custodyGroupCount!.Value : Eip7594DasConstants.CustodyRequirement))
        {
            columns.AddRange(CustodyGroups.ComputeColumnsForCustodyGroup(group));
        }

        return new PeerColumnCustody(columns, advertised);
    }

    /// <summary>The discv5 node id of a libp2p secp256k1 key: keccak256 of the uncompressed 64-byte public key; <c>null</c> for any other key.</summary>
    internal static Hash256? NodeIdOf(Libp2pPublicKey? key) =>
        key is { Type: KeyType.Secp256K1 } && key.Data.Length == CompressedPublicKey.LengthInBytes
            ? new CompressedPublicKey(key.Data.Span).Decompress().Hash
            : null;

    /// <summary>The <c>cgc</c> entry of an ENR text, or <c>null</c> when the text is not a valid ENR or has no such entry.</summary>
    internal static ulong? CustodyGroupCountOf(string? enr)
    {
        if (enr is null)
        {
            return null;
        }

        try
        {
            return BeaconDiscovery.TryGetCustodyGroupCount(NodeRecord.FromEnrString(enr), out ulong count) ? count : null;
        }
        catch (Exception e) when (e is RlpException or ArgumentException or FormatException or InvalidOperationException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The custody a discovered peer advertises in its record: its secp256k1 key's node id and its <c>cgc</c> entry.</summary>
    /// <returns><c>null</c> when the record carries no secp256k1 key.</returns>
    internal static PeerColumnCustody? ForRecord(NodeRecord record) =>
        record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1) is { } key
            ? ForNode(key.Decompress().Hash, BeaconDiscovery.TryGetCustodyGroupCount(record, out ulong count) ? count : null)
            : null;

    /// <summary>As <see cref="ForRecord"/> for an ENR text; <c>null</c> when the text is not a valid ENR with a secp256k1 key.</summary>
    internal static PeerColumnCustody? ForEnr(string? enr)
    {
        if (enr is null)
        {
            return null;
        }

        try
        {
            return ForRecord(NodeRecord.FromEnrString(enr));
        }
        catch (Exception e) when (e is RlpException or ArgumentException or FormatException or InvalidOperationException or InvalidCastException)
        {
            return null;
        }
    }
}
