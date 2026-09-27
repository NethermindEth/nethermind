// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The rolling hash an execution entry commits to: seeded differently on L1 and L2, then folded with tagged
/// execution events. Every fold is <c>keccak256(abi.encodePacked(...))</c> of the parts in order.
/// </summary>
public static class RollingHash
{
    public const byte CallBeginTag = 1;
    public const byte CallEndTag = 2;
    public const byte NestedBeginTag = 3;
    public const byte NestedEndTag = 4;
    public const byte CallNotFoundTag = 5;

    private const int HashSize = 32;
    private const int StackLimit = 256;

    /// <summary><c>keccak256(states || proxyEntryHash)</c>, where <c>states</c> folds each <c>(rollupId, currentState)</c> in order.</summary>
    public static ValueHash256 SeedL1(ReadOnlySpan<StateCommitment> states, in ValueHash256 proxyEntryHash)
    {
        Span<byte> buffer = stackalloc byte[HashSize + sizeof(ulong) + HashSize];
        ValueHash256 statesHash = default;
        foreach (StateCommitment state in states)
        {
            statesHash.Bytes.CopyTo(buffer);
            BinaryPrimitives.WriteUInt64BigEndian(buffer.Slice(HashSize, sizeof(ulong)), state.RollupId);
            state.CurrentState.Bytes.CopyTo(buffer[(HashSize + sizeof(ulong))..]);
            statesHash = ValueKeccak.Compute(buffer);
        }

        return Concat(statesHash, proxyEntryHash);
    }

    /// <summary>The L1 seed of an entry whose single state update is <paramref name="update"/>.</summary>
    public static ValueHash256 SeedL1(StateUpdate update, in ValueHash256 proxyEntryHash) =>
        SeedL1([new StateCommitment(update.RollupId, update.CurrentState)], proxyEntryHash);

    /// <summary>The L2 rolling hash of an entry that delivers one call and records its result.</summary>
    public static ValueHash256 SingleL2Call(in ValueHash256 callHash, bool success, ReadOnlySpan<byte> returnData) =>
        CallEnd(CallBegin(SeedL2(callHash), callHash), success, returnData);

    public static ValueHash256 SeedL2(in ValueHash256 proxyEntryHash) => Concat(default, proxyEntryHash);

    public static ValueHash256 CallBegin(in ValueHash256 previous, in ValueHash256 callHash) => Tagged(previous, CallBeginTag, callHash);

    public static ValueHash256 CallEnd(in ValueHash256 previous, bool success, ReadOnlySpan<byte> returnData) =>
        WithResult(previous, [CallEndTag, success ? (byte)1 : (byte)0], returnData);

    public static ValueHash256 NestedBegin(in ValueHash256 previous, in ValueHash256 callHash) => Tagged(previous, NestedBeginTag, callHash);

    public static ValueHash256 NestedEnd(in ValueHash256 previous)
    {
        Span<byte> buffer = stackalloc byte[HashSize + 1];
        previous.Bytes.CopyTo(buffer);
        buffer[HashSize] = NestedEndTag;
        return ValueKeccak.Compute(buffer);
    }

    public static ValueHash256 CallNotFound(in ValueHash256 previous, in ValueHash256 callHash) => Tagged(previous, CallNotFoundTag, callHash);

    /// <summary>Folds a static call result into the untagged, zero-seeded static accumulator.</summary>
    public static ValueHash256 StaticResult(in ValueHash256 previous, bool success, ReadOnlySpan<byte> returnData) =>
        WithResult(previous, [success ? (byte)1 : (byte)0], returnData);

    private static ValueHash256 Concat(in ValueHash256 first, in ValueHash256 second)
    {
        Span<byte> buffer = stackalloc byte[2 * HashSize];
        first.Bytes.CopyTo(buffer);
        second.Bytes.CopyTo(buffer[HashSize..]);
        return ValueKeccak.Compute(buffer);
    }

    private static ValueHash256 Tagged(in ValueHash256 previous, byte tag, in ValueHash256 callHash)
    {
        Span<byte> buffer = stackalloc byte[HashSize + 1 + HashSize];
        previous.Bytes.CopyTo(buffer);
        buffer[HashSize] = tag;
        callHash.Bytes.CopyTo(buffer[(HashSize + 1)..]);
        return ValueKeccak.Compute(buffer);
    }

    private static ValueHash256 WithResult(in ValueHash256 previous, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> returnData)
    {
        int length = HashSize + prefix.Length + returnData.Length;
        byte[]? rented = length > StackLimit ? ArrayPool<byte>.Shared.Rent(length) : null;
        try
        {
            Span<byte> buffer = rented is null ? stackalloc byte[StackLimit] : rented;
            buffer = buffer[..length];
            previous.Bytes.CopyTo(buffer);
            prefix.CopyTo(buffer[HashSize..]);
            returnData.CopyTo(buffer[(HashSize + prefix.Length)..]);
            return ValueKeccak.Compute(buffer);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
