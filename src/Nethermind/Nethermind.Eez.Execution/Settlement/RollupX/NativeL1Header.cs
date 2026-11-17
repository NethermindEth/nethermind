// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>A canonical L1 header snapshot checked against independently configured fork layouts.</summary>
/// <remarks>Hash linkage and layout validation do not establish L1 consensus or settlement authorization.</remarks>
internal sealed class NativeL1Header
{
    internal const int MaxBytes = 2048;
    private readonly byte[] _encoded;
    internal ulong Number { get; }
    internal ulong Timestamp { get; }
    internal ValueHash256 ParentHash { get; }
    internal ValueHash256 Hash { get; }
    internal ValueHash256 PrevRandao { get; }
    internal ReadOnlyMemory<byte> Encoded => _encoded;

    private NativeL1Header(byte[] encoded, ulong number, ulong timestamp, in ValueHash256 parent, in ValueHash256 prevRandao)
    {
        _encoded = encoded;
        Number = number;
        Timestamp = timestamp;
        ParentHash = parent;
        PrevRandao = prevRandao;
        Hash = ValueKeccak.Compute(encoded);
    }

    internal static NativeL1Header Decode(ReadOnlySpan<byte> input, in NativeL1HeaderRules rules)
    {
        if (input.IsEmpty || input.Length > MaxBytes) throw Invalid("header byte limit");
        byte[] encoded = input.ToArray();
        try
        {
            RlpReader reader = new(encoded);
            int length = reader.ReadSequenceLength();
            if (length != encoded.Length - reader.Position) throw Invalid("trailing or truncated header");
            int fields = reader.PeekNumberOfItemsRemaining(maxSearch: 22);
            if (fields is not (15 or 16 or 17 or 20 or 21)) throw Invalid("unsupported header layout");
            ulong number = 0, timestamp = 0;
            ValueHash256 parent = default, randao = default;
            for (int index = 0; index < fields; index++)
            {
                ReadOnlySpan<byte> value = reader.DecodeByteArraySpan();
                switch (index)
                {
                    case 0: RequireWidth(value, Hash256.Size); parent = new ValueHash256(value); break;
                    case 2: RequireWidth(value, Address.Size); break;
                    case 6: RequireWidth(value, Bloom.ByteLength); break;
                    case 7: RequireInteger(value, 32); break;
                    case 8: number = ReadUInt64(value); break;
                    case 11: timestamp = ReadUInt64(value); break;
                    case 9 or 10 or 15 or 17 or 18: _ = ReadUInt64(value); break;
                    case 12: if (value.Length > 32) throw Invalid("extra data exceeds 32 bytes"); break;
                    case 13: RequireWidth(value, Hash256.Size); randao = new ValueHash256(value); break;
                    case 14: RequireWidth(value, sizeof(ulong)); break;
                    default: RequireWidth(value, Hash256.Size); break;
                }
            }
            if (reader.Position != encoded.Length || !rules.Matches(number, timestamp, fields)) throw Invalid("configured fork layout mismatch");
            return new NativeL1Header(encoded, number, timestamp, in parent, in randao);
        }
        catch (Exception exception) when (exception is RlpException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw Invalid("invalid header RLP");
        }
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> value)
    {
        RequireInteger(value, sizeof(ulong));
        ulong result = 0;
        foreach (byte digit in value) result = (result << 8) | digit;
        return result;
    }

    private static void RequireInteger(ReadOnlySpan<byte> value, int width)
    {
        if (value.Length > width || !value.IsEmpty && value[0] == 0) throw Invalid("noncanonical header integer");
    }

    private static void RequireWidth(ReadOnlySpan<byte> value, int width)
    {
        if (value.Length != width) throw Invalid("invalid header field width");
    }

    internal static EezSettlementException Invalid(string reason) =>
        new(EezSettlementFailure.InvalidDaPayload, $"Invalid native L1 evidence: {reason}.");
}

internal readonly record struct NativeL1HeaderRules(ulong? LondonBlock, ulong? ShanghaiTime, ulong? CancunTime, ulong? PragueTime, ulong? UnsupportedTime)
{
    internal bool Matches(ulong number, ulong timestamp, int fields) =>
        !(UnsupportedTime is { } unsupported && timestamp >= unsupported) &&
        (LondonBlock is { } london && number >= london) == (fields >= 16) &&
        (ShanghaiTime is { } shanghai && timestamp >= shanghai) == (fields >= 17) &&
        (CancunTime is { } cancun && timestamp >= cancun) == (fields >= 20) &&
        (PragueTime is { } prague && timestamp >= prague) == (fields == 21);
}
