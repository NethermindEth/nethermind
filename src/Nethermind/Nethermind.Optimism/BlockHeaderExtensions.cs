// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Optimism;

public readonly struct EIP1559Parameters
{
    public byte Version { get; }
    public UInt32 Denominator { get; }
    public UInt32 Elasticity { get; }

    public EIP1559Parameters(byte version, UInt32 denominator, UInt32 elasticity)
    {
        if (version != 0) throw new ArgumentException($"{nameof(version)} must be 0", nameof(version));
        if (denominator == 0) throw new ArgumentException($"{nameof(denominator)} cannot be 0", nameof(denominator));
        // TODO: Add check for `there is no additional data beyond these 9 bytes` (whatever that means): https://github.com/roberto-bayardo/op-geth/blob/6c32375dda12d3f0b8f3498404f00fe1ae872547/consensus/misc/eip1559/eip1559.go#L112-L114
        // TODO: Revisit this check: https://github.com/roberto-bayardo/op-geth/blob/6c32375dda12d3f0b8f3498404f00fe1ae872547/consensus/misc/eip1559/eip1559.go#L104

        Version = version;
        Denominator = denominator;
        Elasticity = elasticity;
    }
}

public static class BlockHeaderExtensions
{
    public static EIP1559Parameters DecodeEIP1559Parameters(this BlockHeader header)
    {
        if (header.ExtraData.Length < 9) throw new ArgumentException($"{header.ExtraData} data must be at least 9 bytes long");

        ReadOnlySpan<byte> extraData = header.ExtraData.AsSpan();
        var version = extraData.TakeAndMove(1)[0];
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));

        return new EIP1559Parameters(version, denominator, elasticity);
    }
}
