// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public readonly struct EIP1559Parameters(byte version, UInt32 denominator, UInt32 elasticity)
{
    public static readonly byte[] ByteLengthByVersion = [9, 17];

    public byte Version { get; } = version;
    public UInt32 Denominator { get; } = denominator;
    public UInt32 Elasticity { get; } = elasticity;
    public UInt64 MinBaseFee { get; }

    public int ByteLength => ByteLengthByVersion[Version];

    public EIP1559Parameters(byte version, UInt32 denominator, UInt32 elasticity, UInt64 minBaseFee) : this(version, denominator, elasticity) => MinBaseFee = minBaseFee;

    public static bool TryCreateV0(UInt32 denominator, UInt32 elasticity, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        error = null;
        parameters = default;

        if (denominator == 0 && elasticity != 0)
        {
            error = $"{nameof(denominator)} cannot be 0 unless {nameof(elasticity)} is also 0";
            return false;
        }

        if (denominator != 0 && elasticity == 0)
        {
            error = $"{nameof(elasticity)} cannot be 0 unless {nameof(denominator)} is also 0";
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

        if (denominator == 0 && (elasticity != 0 || minBaseFee != 0))
        {
            error = $"{nameof(denominator)} cannot be 0 unless {nameof(elasticity)} and {nameof(minBaseFee)} are also 0";
            return false;
        }

        if (denominator != 0 && elasticity == 0)
        {
            error = $"{nameof(elasticity)} cannot be 0 unless {nameof(denominator)} is also 0";
            return false;
        }

        parameters = new EIP1559Parameters(1, denominator, elasticity, minBaseFee);

        return true;
    }

    public bool IsZero() => Denominator == 0 && Elasticity == 0 && MinBaseFee == 0;

    public void WriteTo(Span<byte> span)
    {
        span[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1, sizeof(UInt32)), Denominator);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(5, sizeof(UInt32)), Elasticity);

        if (Version >= 1)
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(9, sizeof(UInt64)), MinBaseFee);
    }

    public override string ToString() => Version == 0
        ? $"{nameof(EIP1559Parameters)}(denominator: {Denominator}, elasticity: {Elasticity})"
        : $"{nameof(EIP1559Parameters)}(denominator: {Denominator}, elasticity: {Elasticity}, minBaseFee: {MinBaseFee})";
}

public static class EIP1559ParametersExtensions
{
    private const int AttributesEIP1559ParamsLength = 8;

    public static bool TryDecodeEIP1559Parameters(this BlockHeader header, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        parameters = default;

        ReadOnlySpan<byte> data = header.ExtraData;
        int dataLength = data.Length;
        if (dataLength == 0)
        {
            error = $"{nameof(header.ExtraData)} must not be empty";
            return false;
        }

        int maxVersion = EIP1559Parameters.ByteLengthByVersion.Length - 1;
        byte version = data.TakeAndMove(1)[0];
        if (version > maxVersion)
        {
            error = $"{nameof(version)} must be between 0 and {maxVersion}";
            return false;
        }

        byte expLength = EIP1559Parameters.ByteLengthByVersion[version];
        if (dataLength != expLength)
        {
            error = $"{nameof(header.ExtraData)} must be {expLength} bytes long on version {version}";
            return false;
        }

        UInt32 denominator = BinaryPrimitives.ReadUInt32BigEndian(data.TakeAndMove(sizeof(UInt32)));
        UInt32 elasticity = BinaryPrimitives.ReadUInt32BigEndian(data.TakeAndMove(sizeof(UInt32)));

        if (version == 0)
        {
            return EIP1559Parameters.TryCreateV0(denominator, elasticity, out parameters, out error);
        }

        UInt64 minBaseFee = BinaryPrimitives.ReadUInt64BigEndian(data.TakeAndMove(sizeof(UInt64)));
        return EIP1559Parameters.TryCreateV1(denominator, elasticity, minBaseFee, out parameters, out error);
    }

    public static bool TryDecodeEIP1559Parameters(this OptimismPayloadAttributes attributes, out EIP1559Parameters parameters, [NotNullWhen(false)] out string? error)
    {
        parameters = default;

        ReadOnlySpan<byte> data = attributes.EIP1559Params;
        if (data.Length != AttributesEIP1559ParamsLength)
        {
            error = $"{nameof(attributes.EIP1559Params)} must be {AttributesEIP1559ParamsLength} bytes long";
            return false;
        }

        UInt32 denominator = BinaryPrimitives.ReadUInt32BigEndian(data.TakeAndMove(sizeof(UInt32)));
        UInt32 elasticity = BinaryPrimitives.ReadUInt32BigEndian(data.TakeAndMove(sizeof(UInt32)));

        if (attributes.MinBaseFee is not { } minBaseFee)
        {
            return EIP1559Parameters.TryCreateV0(denominator, elasticity, out parameters, out error);
        }

        // Zero denominator and elasticity request the chain defaults; the minimum base fee is set independently.
        if (denominator == 0 && elasticity == 0)
        {
            parameters = new EIP1559Parameters(1, denominator, elasticity, minBaseFee);
            error = null;
            return true;
        }

        return EIP1559Parameters.TryCreateV1(denominator, elasticity, minBaseFee, out parameters, out error);
    }

    /// <summary>Splits Holocene or Jovian header extraData into payload attributes' <c>eip1559Params</c> and <c>minBaseFee</c>.</summary>
    internal static (byte[] EIP1559Params, ulong? MinBaseFee) SplitHeaderExtraData(ReadOnlySpan<byte> extraData)
    {
        ReadOnlySpan<byte> parameters = extraData[1..];
        return parameters.Length > AttributesEIP1559ParamsLength
            ? (parameters[..AttributesEIP1559ParamsLength].ToArray(), BinaryPrimitives.ReadUInt64BigEndian(parameters[AttributesEIP1559ParamsLength..]))
            : (parameters.ToArray(), null);
    }
}
