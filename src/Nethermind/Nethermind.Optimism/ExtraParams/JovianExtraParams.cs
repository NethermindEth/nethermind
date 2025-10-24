// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.ExtraParams;

public readonly struct JovianExtraParams
{
    public const int BinaryLength = 17;

    public required UInt32 Denominator { get; init; }
    public required UInt32 Elasticity { get; init; }
    public required long MinimumBaseFee { get; init; }

    public bool IsZero() => Denominator == 0 && Elasticity == 0;

    public static bool TryParse(BlockHeader header, out JovianExtraParams parameters, [NotNullWhen(false)] out string? error)
    {
        error = null;
        parameters = default;

        if (header.ExtraData.Length != BinaryLength)
        {
            error = $"{nameof(header.ExtraData)} data must be {BinaryLength} bytes long";
            return false;
        }

        ReadOnlySpan<byte> extraData = header.ExtraData.AsSpan();
        var version = extraData.TakeAndMove(1)[0];

        if (version != 1)
        {
            error = $"{nameof(version)} must be 1";
            return false;
        }

        var denominator = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(extraData.TakeAndMove(4));

        if (denominator == 0 && elasticity != 0)
        {
            error = $"{nameof(denominator)} must not be 0";
            return false;
        }

        var minimumBaseFee = BinaryPrimitives.ReadInt64BigEndian(extraData.TakeAndMove(8));

        parameters = new JovianExtraParams { Denominator = denominator, Elasticity = elasticity, MinimumBaseFee = minimumBaseFee };
        return true;
    }

    public static bool TryParse(OptimismPayloadAttributes payloadAttributes, out JovianExtraParams parameters, [NotNullWhen(false)] out string? error)
    {
        error = null;
        parameters = default;

        if (payloadAttributes.EIP1559Params?.Length != 8)
        {
            error = $"{nameof(payloadAttributes.EIP1559Params)} must be 8 bytes long";
            return false;
        }

        ReadOnlySpan<byte> span = payloadAttributes.EIP1559Params.AsSpan();
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
        var elasticity = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));

        if (payloadAttributes.MinimumBaseFee is null)
        {
            error = $"{nameof(payloadAttributes.MinimumBaseFee)} is missing";
            return false;
        }
        var minimumBaseFee = payloadAttributes.MinimumBaseFee.Value;

        parameters = new JovianExtraParams { Denominator = denominator, Elasticity = elasticity, MinimumBaseFee = minimumBaseFee };
        return true;
    }

    public void WriteTo(Span<byte> span)
    {
        span[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1, 4), Denominator);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(5, 4), Elasticity);
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(9, 8), MinimumBaseFee);
    }
}
