// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
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
        Version = version;
        Denominator = denominator;
        Elasticity = elasticity;
    }

    public static bool TryCreate(byte version, UInt32 denominator, UInt32 elasticity, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        error = null;
        parameters = default;

        if (version != 0)
        {
            error = $"{nameof(version)} must be 0";
            return false;
        }

        if (denominator == 0 && elasticity != 0)
        {
            error = $"{nameof(denominator)} cannot be 0 unless {nameof(elasticity)} is also 0";
            return false;
        }

        parameters = new EIP1559Parameters(version, denominator, elasticity);
        return true;
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
    public static bool TryDecodeEIP1559Parameters(this BlockHeader header, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        if (header.ExtraData.Length != EIP1559Parameters.ByteLength)
        {
            parameters = default;
            error = $"{nameof(header.ExtraData)} data must be {EIP1559Parameters.ByteLength} bytes long";
            return false;
        }

        ReadOnlySpan<byte> extraData = header.ExtraData.AsSpan();
        var version = extraData.TakeAndMove(1)[0];
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));

        return EIP1559Parameters.TryCreate(version, denominator, elasticity, out parameters, out error);
    }

    public static bool TryDecodeEIP1559Parameters(this OptimismPayloadAttributes attributes, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        if (attributes.EIP1559Params?.Length != 8)
        {
            parameters = default;
            error = $"{nameof(attributes.EIP1559Params)} must be 8 bytes long";
            return false;
        }

        ReadOnlySpan<byte> span = attributes.EIP1559Params.AsSpan();
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));

        return EIP1559Parameters.TryCreate(0, denominator, elasticity, out parameters, out error);
    }
}
