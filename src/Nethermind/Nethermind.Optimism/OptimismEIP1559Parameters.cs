// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public readonly struct EIP1559Parameters
{
    public const int ByteLength = 9;

    public byte Version { get; }
    public UInt32 Denominator { get; }
    public UInt32 Elasticity { get; }

    public EIP1559Parameters(byte version, UInt32 denominator, UInt32 elasticity)
    {
        if (version != 0) throw new ArgumentException($"{nameof(version)} must be 0", nameof(version));
        if (denominator == 0 && elasticity != 0) throw new ArgumentException($"{nameof(denominator)} cannot be 0 unless {nameof(elasticity)} is also 0", nameof(denominator));

        Version = version;
        Denominator = denominator;
        Elasticity = elasticity;
    }

    public bool IsZero() => Denominator == 0 && Elasticity == 0;

    public void WriteTo(Span<byte> span)
    {
        span[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1, 4), Denominator);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(5, 4), Elasticity);
    }
}

public static class EIP1559ParametersExtensions
{
    public static EIP1559Parameters DecodeEIP1559Parameters(this BlockHeader header)
    {
        if (header.ExtraData.Length < EIP1559Parameters.ByteLength) throw new ArgumentException($"{nameof(header.ExtraData)} data must be at least 9 bytes long");
        // TODO: Add check for `there is no additional data beyond these 9 bytes` (whatever that means): https://github.com/roberto-bayardo/op-geth/blob/6c32375dda12d3f0b8f3498404f00fe1ae872547/consensus/misc/eip1559/eip1559.go#L112-L114

        ReadOnlySpan<byte> extraData = header.ExtraData.AsSpan();
        var version = extraData.TakeAndMove(1)[0];
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));

        return new EIP1559Parameters(version, denominator, elasticity);
    }

    public static EIP1559Parameters DecodeEIP1559Parameters(this OptimismPayloadAttributes attributes)
    {
        if (attributes.EIP1559Params?.Length != 8) throw new ArgumentException($"{nameof(attributes.EIP1559Params)} must be 8 bytes long");

        ReadOnlySpan<byte> span = attributes.EIP1559Params.AsSpan();
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));

        return new EIP1559Parameters(0, denominator, elasticity);
    }
}
