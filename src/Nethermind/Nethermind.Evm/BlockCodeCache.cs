// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>An <see cref="ICodeCache"/> that also keeps the code a block loads until <see cref="ClearBlock"/>.</summary>
/// <remarks>
/// A warm CALL costs 100 gas whatever the size of the code, so a block can call more distinct large contracts, round
/// and round, than the process-wide cache holds; each call would then read and analyse its code again. Code loaded
/// during the block is kept here, up to a cap on the memory it retains. Past the cap, code is not taken in rather than
/// evicting other code, as evicting on a cycle over a set larger than the cache misses on every load.
/// The process-wide cache is probed first, so a block it serves pays nothing here.
/// </remarks>
public sealed class BlockCodeCache : ICodeCache
{
    /// <summary>The default cap on retained memory: about 14k contracts of 64 KiB.</summary>
    public static readonly long DefaultMaxBytes = 1.GiB;

    /// <summary>Charged per entry on top of its code and jump-destination bitmap: the objects, padding and dictionary entry.</summary>
    internal const int EntryOverheadBytes = 256;

    private readonly ICodeCache _inner;
    private readonly long _maxBytes;
    private readonly Retained _retained;

    /// <param name="inner">The process-wide cache.</param>
    public BlockCodeCache(ICodeCache inner) : this(inner, DefaultMaxBytes) { }

    /// <param name="inner">The process-wide cache.</param>
    /// <param name="maxBytes">The most memory the block's code retains, in bytes.</param>
    public BlockCodeCache(ICodeCache inner, long maxBytes) : this(inner, maxBytes, new Retained()) { }

    private BlockCodeCache(ICodeCache inner, long maxBytes, Retained retained)
    {
        _inner = inner;
        _maxBytes = maxBytes;
        _retained = retained;
    }

    /// <summary>The memory charged for the block's code, in bytes.</summary>
    internal long Bytes => Volatile.Read(ref _retained.Bytes);

    /// <summary>A view of the same block's code that takes code in only while the block retains less than <paramref name="maxBytes"/>.</summary>
    /// <remarks>Lets warming share the block's code without spending the budget of the block's own execution.</remarks>
    public BlockCodeCache WithLimit(long maxBytes) => new(_inner, maxBytes, _retained);

    public CodeInfo? Get(in ValueHash256 codeHash) =>
        _inner.Get(in codeHash)
        ?? (Volatile.Read(ref _retained.Bytes) != 0 && _retained.Code.TryGetValue(codeHash, out CodeInfo? codeInfo) ? codeInfo : null);

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        _inner.Set(in codeHash, codeInfo);

        // Racing loads may each pass the check, overshooting the cap by at most one code per thread.
        long charge = codeInfo.CodeLength + (codeInfo.CodeLength >> 3) + EntryOverheadBytes;
        if (Volatile.Read(ref _retained.Bytes) + charge <= _maxBytes && _retained.Code.TryAdd(codeHash, codeInfo))
        {
            Interlocked.Add(ref _retained.Bytes, charge);
        }
    }

    /// <summary>Drops the code kept for the block, for every view of it.</summary>
    public void ClearBlock()
    {
        _retained.Code.Clear();
        Volatile.Write(ref _retained.Bytes, 0);
    }

    public void Clear()
    {
        ClearBlock();
        _inner.Clear();
    }

    private sealed class Retained
    {
        public readonly ConcurrentDictionary<ValueHash256, CodeInfo> Code = new();
        public long Bytes;
    }
}
