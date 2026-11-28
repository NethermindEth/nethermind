// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>
/// Remembers the bytes, the Keccak hash and the analysed <see cref="CodeInfo"/> of code that arrives in state overrides.
/// </summary>
/// <remarks>
/// RPC clients replaying calls send the same override code again and again, and every call decoded it into a new
/// array, hashed it and built (and later jump-analysed) a fresh <see cref="CodeInfo"/>. A small set-associative table
/// keyed by a fast content hash answers repeats; every hit is verified byte for byte, so the array, the hash and the
/// CodeInfo always belong to exactly the bytes the caller passed. Entries are immutable and replaced whole, so
/// concurrent requests at worst compute an entry twice. A shared CodeInfo has no per-call state other than its lazily
/// built jump-destination bitmap, which uses the same protocol that lets the code cache share CodeInfo
/// between block processing and RPC.
/// </remarks>
internal static class OverrideCodeCache
{
    private const int Sets = 64; // power of two
    // A call overrides tens of contracts at once, so two of them sharing a direct-mapped slot evict each other on every call.
    internal const int Ways = 4;
    internal const int MaxCachedLength = 64 * 1024;

    private sealed class Entry(byte[] code, in ValueHash256 hash, CodeInfo info)
    {
        // The CodeInfo replaces its reference to the array with a padded copy once it runs.
        public readonly byte[] Code = code;
        public readonly ValueHash256 Hash = hash;
        public readonly CodeInfo Info = info;
    }

    [InlineArray(Sets * Ways)]
    private struct EntryTable
    {
        private Entry? _element0;
    }

    private static EntryTable _entries;
    private static uint _nextVictim;

    /// <summary>Returns the Keccak hash and a <see cref="CodeInfo"/> of <paramref name="code"/>.</summary>
    /// <param name="code">
    /// Kept by reference and served to later callers with equal bytes, so it must not be modified afterwards,
    /// as the world state already assumes of override code.
    /// </param>
    public static void Resolve(byte[] code, out ValueHash256 codeHash, out CodeInfo codeInfo)
    {
        if (code.Length == 0 || code.Length > MaxCachedLength)
        {
            codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code);
            codeInfo = new CodeInfo(code);
            return;
        }

        int set = SetOf(code);
        Entry? entry = Find(set, code);
        if (entry is null)
        {
            entry = new Entry(code, ValueKeccak.Compute(code), new CodeInfo(code));
            Store(set, entry);
        }

        codeHash = entry.Hash;
        codeInfo = entry.Info;
    }

    /// <summary>Gets the array an earlier <see cref="Resolve"/> kept for exactly the bytes in <paramref name="code"/>.</summary>
    /// <remarks>Lets a caller that decoded the code into a scratch buffer skip allocating its own copy.</remarks>
    public static bool TryGetCached(ReadOnlySpan<byte> code, [NotNullWhen(true)] out byte[]? cached)
    {
        cached = code.Length == 0 || code.Length > MaxCachedLength ? null : Find(SetOf(code), code)?.Code;
        return cached is not null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SetOf(ReadOnlySpan<byte> code) => (code.FastHash() & (Sets - 1)) * Ways;

    private static Entry? Find(int set, ReadOnlySpan<byte> code)
    {
        for (int way = 0; way < Ways; way++)
        {
            Entry? entry = Volatile.Read(ref _entries[set + way]);
            if (entry is not null && entry.Code.AsSpan().SequenceEqual(code)) return entry;
        }

        return null;
    }

    private static void Store(int set, Entry entry)
    {
        for (int way = 0; way < Ways; way++)
        {
            ref Entry? slot = ref _entries[set + way];
            if (Volatile.Read(ref slot) is null)
            {
                Volatile.Write(ref slot, entry);
                return;
            }
        }

        // A racing increment only changes which way is replaced.
        Volatile.Write(ref _entries[set + (int)(_nextVictim++ % Ways)], entry);
    }
}
