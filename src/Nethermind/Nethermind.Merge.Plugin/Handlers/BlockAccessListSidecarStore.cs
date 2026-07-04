// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// EIP-8146: holds RLP-encoded block access list sidecars delivered via
/// <c>engine_notifyBlockAccessListV1</c> until the matching payload arrives, keyed by
/// <c>keccak256(rlp(BAL))</c> — the header's <c>block_access_list_hash</c> commitment.
/// </summary>
public interface IBlockAccessListSidecarStore
{
    /// <summary>Stores a sidecar under its keccak commitment.</summary>
    void Add(byte[] rlpEncodedBal);

    bool TryGet(Hash256 blockAccessListHash, out byte[]? rlpEncodedBal);
}

public class BlockAccessListSidecarStore : IBlockAccessListSidecarStore
{
    // A handful of slots covers the reorg window; sidecars are ~70 KiB each.
    private const int Capacity = 64;

    private readonly LruCache<ValueHash256, byte[]> _sidecars = new(Capacity, "EIP-8146 BAL sidecars");

    public void Add(byte[] rlpEncodedBal) => _sidecars.Set(ValueKeccak.Compute(rlpEncodedBal), rlpEncodedBal);

    public bool TryGet(Hash256 blockAccessListHash, out byte[]? rlpEncodedBal) =>
        _sidecars.TryGet(blockAccessListHash.ValueHash256, out rlpEncodedBal);
}
