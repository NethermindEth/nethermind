// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public readonly struct EIP1559Parameters
{
    public static readonly byte[] ByteLengthByVersion = [9, 17];

    public byte Version { get; }
    public UInt32 Denominator { get; }
    public UInt32 Elasticity { get; }
    public UInt64? MinBaseFee { get; } // TODO: tests for when this field is present

    public int ByteLength => ByteLengthByVersion[Version];

    public EIP1559Parameters(byte version, UInt32 denominator, UInt32 elasticity)
    {
        Version = version;
        Denominator = denominator;
        Elasticity = elasticity;
    }

    public EIP1559Parameters(byte version, UInt32 denominator, UInt32 elasticity, UInt64? minBaseFee) : this(version, denominator, elasticity)
    {
        MinBaseFee = minBaseFee;
    }

    public static bool TryCreateV0(UInt32 denominator, UInt32 elasticity, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        error = null;
        parameters = default;

        if (denominator == 0 && elasticity != 0)
        {
            error = $"{nameof(denominator)} cannot be 0 unless {nameof(elasticity)} is also 0";
            return false;
        }

        parameters = new EIP1559Parameters(0, denominator, elasticity);

        return true;
    }

    public static bool TryCreateV1(uint denominator, uint elasticity, UInt64 minBaseFee, out EIP1559Parameters parameters,
        [NotNullWhen(false)] out string? error
    )
    {
        error = null;
        parameters = default;

        if (denominator == 0)
        {
            error = $"{nameof(denominator)} cannot be 0";
            return false;
        }

        parameters = new EIP1559Parameters(1, denominator, elasticity, minBaseFee);

        return true;
    }

    public bool IsZero() => Version == 0 && Denominator == 0 && Elasticity == 0 && MinBaseFee == null;

    public void WriteTo(Span<byte> span)
    {
        span[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1, sizeof(UInt32)), Denominator);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(5, sizeof(UInt32)), Elasticity);

        if (MinBaseFee is {} minBaseFee)
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(9, sizeof(UInt64)), minBaseFee);
    }
}

public static class EIP1559ParametersExtensions
{
    public static bool TryDecodeEIP1559Parameters(this BlockHeader header, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        return TryDecodeEIP1559Parameters(header.ExtraData, out parameters, out error);
    }

    public static bool TryDecodeEIP1559Parameters(this OptimismPayloadAttributes attributes, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        return TryDecodeEIP1559Parameters(attributes.EIP1559Params, out parameters, out error);
    }

    private static bool TryDecodeEIP1559Parameters(
        ReadOnlySpan<byte> data, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error,
        [CallerArgumentExpression(nameof(data))] string dataName = "data"
    )
    {
        if (data.Length == 0)
            error = $"{dataName} must not be empty";

        parameters = default;

        var version = data.TakeAndMove(1)[0];
        if (version >= EIP1559Parameters.ByteLengthByVersion.Length)
        {
            error = $"{nameof(version)} must be between 0 and {EIP1559Parameters.ByteLengthByVersion.Length - 1}";
            return false;
        }

        var length = EIP1559Parameters.ByteLengthByVersion[version];
        if (data.Length != length)
        {
            parameters = default;
            error = $"{dataName} must be {length} bytes long";
            return false;
        }

        var denominator = BinaryPrimitives.ReadUInt32BigEndian(data.TakeAndMove(sizeof(UInt32)));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(data.TakeAndMove(sizeof(UInt32)));

        if (version == 0)
            return EIP1559Parameters.TryCreateV0(denominator, elasticity, out parameters, out error);

        var minBaseFee = BinaryPrimitives.ReadUInt64BigEndian(data.TakeAndMove(sizeof(UInt64)));
        return EIP1559Parameters.TryCreateV1(denominator, elasticity, minBaseFee, out parameters, out error);
    }
}
