// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>
/// Remembers the Keccak hash and the analysed <see cref="CodeInfo"/> of code that arrives in state overrides.
/// </summary>
/// <remarks>
/// RPC clients replaying calls send the same override code again and again, and every call hashed it and
/// built (and later jump-analysed) a fresh <see cref="CodeInfo"/>. A small set-associative table keyed by a fast
/// content hash answers repeats; every hit is verified byte for byte, so the hash and the CodeInfo always
/// belong to exactly the bytes the caller passed. A set holds <see cref="Ways"/> codes: with one slot per code,
/// two codes a request always sends together could meet in a slot, depending on the process-random hash seed, and
/// then both missed on every request. Entries are immutable, and concurrent misses of one code store it once.
/// A shared CodeInfo has no per-call state other than its lazily built jump-destination bitmap, which uses the
/// same protocol that lets the code cache share CodeInfo between block processing and RPC.
/// </remarks>
internal sealed class OverrideCodeCache
{
    internal const int Sets = 64; // power of two
    internal const int Ways = 4;
    private const int MaxCachedLength = 64 * 1024;

    private static readonly OverrideCodeCache Shared = new();

    private sealed class Entry(int fastHash, in ValueHash256 hash, CodeInfo info) : SetAssociativeEntry(fastHash)
    {
        public readonly ValueHash256 Hash = hash;
        public readonly CodeInfo Info = info;

        public override ReadOnlySpan<byte> Key => Info.CodeSpan;
    }

    private readonly SetAssociativeTable<Entry> _entries = new(Sets, Ways);

    internal int Count => _entries.Count;

    /// <summary>Returns the Keccak hash and a <see cref="CodeInfo"/> of <paramref name="code"/>.</summary>
    /// <param name="code">
    /// Kept by reference and served to later callers with equal bytes, so it must not be modified afterwards,
    /// as the world state already assumes of override code.
    /// </param>
    public static void Resolve(byte[] code, out ValueHash256 codeHash, out CodeInfo codeInfo) =>
        Shared.Get(code, out codeHash, out codeInfo);

    /// <inheritdoc cref="Resolve"/>
    internal void Get(byte[] code, out ValueHash256 codeHash, out CodeInfo codeInfo)
    {
        if (code.Length == 0 || code.Length > MaxCachedLength)
        {
            codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code);
            codeInfo = new CodeInfo(code);
            return;
        }

        Get(code, ((ReadOnlySpan<byte>)code).FastHash(), out codeHash, out codeInfo);
    }

    /// <summary><see cref="Get(byte[], out ValueHash256, out CodeInfo)"/> with the fast hash that picks the set given.</summary>
    /// <remarks>Tests pass their own hash to put codes in one set.</remarks>
    internal void Get(byte[] code, int fastHash, out ValueHash256 codeHash, out CodeInfo codeInfo)
    {
        Entry entry = _entries.Find(code, fastHash)
            ?? _entries.GetOrAdd(new Entry(fastHash, ValueKeccak.Compute(code), new CodeInfo(code)));
        codeHash = entry.Hash;
        codeInfo = entry.Info;
    }

    internal bool Contains(byte[] code, int fastHash) => _entries.Find(code, fastHash) is not null;
}
