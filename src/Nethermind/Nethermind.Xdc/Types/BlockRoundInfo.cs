// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc.Types;

public class BlockRoundInfo(Hash256 hash256, ulong round, long number) : IEquatable<BlockRoundInfo>
{
    public Hash256 Hash { get; set; } = hash256;
    public ulong Round { get; set; } = round;
    public long BlockNumber { get; set; } = number;
    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);

    public bool Equals(BlockRoundInfo? other) =>
        other is not null &&
        Hash == other.Hash &&
        Round == other.Round &&
        BlockNumber == other.BlockNumber;

    public override bool Equals(object? obj) => Equals(obj as BlockRoundInfo);

    public override int GetHashCode() => HashCode.Combine(Hash, Round, BlockNumber);
}
