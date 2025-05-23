// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Optimism.CL;

public readonly struct L1Block
{
    public byte[] ExtraData { get; init; }
    public Hash256 Hash { get; init; }
    public Hash256 ParentHash { get; init; }
    public UInt256 Timestamp { get; init; }
    public L1Transaction[]? Transactions { get; init; }
    public ulong Number { get; init; }
    public Hash256? ParentBeaconBlockRoot { get; init; }
    public ulong? ExcessBlobGas { get; init; }
    public UInt256? BaseFeePerGas { get; init; }
    public Hash256 MixHash { get; init; }
}

public readonly struct L1Transaction
{
    public Hash256? Hash { get; init; }
    public TxType? Type { get; init; }
    public Address? From { get; init; }
    public Address? To { get; init; }
    public byte[][]? BlobVersionedHashes { get; init; }
    public byte[]? Input { get; init; }
}

/// <remarks>
/// https://specs.optimism.io/protocol/rollup-node.html?utm_source=op-docs&utm_medium=docs#l1blockref
/// </remarks>
public sealed record L1BlockRef
{
    public required Hash256 Hash { get; init; }
    public required ulong Number { get; init; }
    public required Hash256 ParentHash { get; init; }
    public required ulong Timestamp { get; init; }

    public static L1BlockRef Zero => new()
    {
        Hash = Hash256.Zero,
        Number = 0,
        ParentHash = Hash256.Zero,
        Timestamp = 0
    };

    public static L1BlockRef From(L1BlockInfo blockInfo)
    {
        return new L1BlockRef
        {
            Hash = blockInfo.BlockHash,
            Number = blockInfo.Number,
            ParentHash = blockInfo.BlockHash,
            Timestamp = blockInfo.Timestamp
        };
    }

    public static L1BlockRef From(L1Block? block)
    {
        return block is null ? Zero : new L1BlockRef
        {
            Hash = block.Value.Hash,
            Number = block.Value.Number,
            ParentHash = block.Value.ParentHash,
            Timestamp = (ulong)block.Value.Timestamp // TODO: Potential unsafe cast
        };
    }
}
