// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Experimental memo of depth-1 subcall results for cancelable call frames, keyed by everything a
/// clean-prefix sibling can observe: block (hash covers every environment opcode), transaction
/// origin and gas price, code source, target, the exact forwarded gas and the input. A memo is
/// recorded only for a child that reverted - such a frame leaves no state, no logs, no refunds and,
/// per EIP-2929 journaling, no access-list warmth - and replayed only while every earlier sibling
/// of the current frame also left nothing, which makes the recorded and replayed entry
/// environments provably identical. Off unless NETHERMIND_SUBCALL_MEMO=1; a transaction-level
/// access list can pre-warm child reads and is not yet part of the key, which the experiment gate
/// accepts and productization must address.
/// </summary>
public static class SubcallMemo
{
    public static readonly bool IsEnabled = Environment.GetEnvironmentVariable("NETHERMIND_SUBCALL_MEMO") == "1";

    private static readonly ClockCache<ValueHash256, Entry> s_cache = new(1024 * 16);

    public sealed class Entry
    {
        public byte[] Output { get; init; }
        public ulong GasSpent { get; init; }
    }

    public static ValueHash256 ComputeKey(
        in ValueHash256 blockHash,
        in ValueHash256 origin,
        in UInt256 gasPrice,
        Address codeSource,
        Address target,
        ulong gasGiven,
        ReadOnlySpan<byte> input)
    {
        ValueHash256 inputHash = ValueKeccak.Compute(input);
        Span<byte> material = stackalloc byte[32 + 32 + 32 + 20 + 20 + 8 + 32];
        blockHash.Bytes.CopyTo(material);
        origin.Bytes.CopyTo(material[32..]);
        gasPrice.ToBigEndian(material[64..96]);
        codeSource.Bytes.CopyTo(material[96..]);
        target.Bytes.CopyTo(material[116..]);
        BinaryPrimitives.WriteUInt64LittleEndian(material[136..], gasGiven);
        inputHash.Bytes.CopyTo(material[144..]);
        return ValueKeccak.Compute(material);
    }

    public static bool TryGet(in ValueHash256 key, out Entry entry)
    {
        entry = s_cache.Get(key);
        return entry is not null;
    }

    public static void Record(in ValueHash256 key, byte[] output, ulong gasSpent) =>
        s_cache.Set(key, new Entry { Output = output, GasSpent = gasSpent });
}
