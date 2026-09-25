// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>
/// Remembers the Keccak hash and the analysed <see cref="CodeInfo"/> of code that arrives in state overrides.
/// </summary>
/// <remarks>
/// RPC clients replaying calls send the same override code again and again, and every call hashed it and
/// built (and later jump-analysed) a fresh <see cref="CodeInfo"/>. A small direct-mapped table keyed by a fast
/// content hash answers repeats; every hit is verified byte for byte, so the hash and the CodeInfo always
/// belong to exactly the bytes the caller passed. Entries are immutable and replaced whole, so concurrent
/// requests at worst compute an entry twice. CodeInfo is already shared across threads by the code cache.
/// </remarks>
internal static class OverrideCodeCache
{
    private const int Slots = 256; // power of two
    private const int MaxCachedLength = 64 * 1024;

    private sealed class Entry(byte[] code, in ValueHash256 hash, CodeInfo info)
    {
        public readonly byte[] Code = code;
        public readonly ValueHash256 Hash = hash;
        public readonly CodeInfo Info = info;
    }

    private static readonly Entry?[] _entries = new Entry?[Slots];

    public static void Resolve(byte[] code, out ValueHash256 codeHash, out CodeInfo codeInfo)
    {
        if (code.Length == 0 || code.Length > MaxCachedLength)
        {
            codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code);
            codeInfo = new CodeInfo(code);
            return;
        }

        ref Entry? slot = ref _entries[((ReadOnlySpan<byte>)code).FastHash() & (Slots - 1)];
        Entry? entry = Volatile.Read(ref slot);
        if (entry is not null && entry.Code.AsSpan().SequenceEqual(code))
        {
            codeHash = entry.Hash;
            codeInfo = entry.Info;
            return;
        }

        codeHash = ValueKeccak.Compute(code);
        codeInfo = new CodeInfo(code);
        Volatile.Write(ref slot, new Entry(code, codeHash, codeInfo));
    }
}
