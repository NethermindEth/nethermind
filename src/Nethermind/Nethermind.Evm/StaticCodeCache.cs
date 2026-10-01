// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>LRU <see cref="ICodeCache"/>; <see cref="Instance"/> is the process-wide one used for normal block processing and reset between shared-cache test runs.</summary>
/// <remarks>The single-tier constructor is for short-lived caches, such as one block's or a test's; overflowing one only
/// costs a re-read and re-analysis of the code.</remarks>
public sealed class StaticCodeCache : ICodeCache
{
    /// <summary>The largest code the small tier holds.</summary>
    public const int SmallCodeSize = 8 * 1024;

    /// <summary>The largest code the medium tier holds: the EIP-170 limit that bounded all code before EIP-7907.</summary>
    public const int MediumCodeSize = 24 * 1024;

    public static readonly StaticCodeCache Instance = new(
        MemoryAllowance.SmallCodeCacheSize, MemoryAllowance.MediumCodeCacheSize, MemoryAllowance.LargeCodeCacheSize);

    private readonly AssociativeCache<ValueHash256, CodeInfo> _small;
    private readonly AssociativeCache<ValueHash256, CodeInfo>? _medium;
    private readonly AssociativeCache<ValueHash256, CodeInfo>? _large;

    /// <summary>Holds code of any size in one tier of <paramref name="maxCapacity"/> entries.</summary>
    public StaticCodeCache(int maxCapacity) => _small = new AssociativeCache<ValueHash256, CodeInfo>(maxCapacity);

    /// <summary>Holds code in a tier by its size, so large code only evicts large code.</summary>
    /// <remarks>
    /// Code above <see cref="MediumCodeSize"/> is what a block of distinct maximum-size contracts loads; confined to
    /// its own tier, it cannot evict the small and medium code ordinary blocks run, and each tier bounds its memory
    /// by the largest code it holds.
    /// </remarks>
    /// <param name="smallCapacity">Entries for code of at most <see cref="SmallCodeSize"/>.</param>
    /// <param name="mediumCapacity">Entries for code of at most <see cref="MediumCodeSize"/>.</param>
    /// <param name="largeCapacity">Entries for larger code.</param>
    public StaticCodeCache(int smallCapacity, int mediumCapacity, int largeCapacity)
    {
        _small = new AssociativeCache<ValueHash256, CodeInfo>(smallCapacity);
        _medium = new AssociativeCache<ValueHash256, CodeInfo>(mediumCapacity);
        _large = new AssociativeCache<ValueHash256, CodeInfo>(largeCapacity);
    }

    public CodeInfo? Get(in ValueHash256 codeHash)
    {
        // Hashed once for every tier a miss probes.
        long hashCode = codeHash.GetHashCode64();
        return _small.TryGet(in codeHash, hashCode, out CodeInfo? codeInfo)
            || _medium?.TryGet(in codeHash, hashCode, out codeInfo) == true
            || _large?.TryGet(in codeHash, hashCode, out codeInfo) == true
            ? codeInfo
            : null;
    }

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        codeInfo.StampCodeHash(in codeHash);
        TierFor(codeInfo.CodeLength).Set(in codeHash, codeInfo);
    }

    public void Clear()
    {
        _small.Clear();
        _medium?.Clear();
        _large?.Clear();
    }

    private AssociativeCache<ValueHash256, CodeInfo> TierFor(int codeLength) =>
        _medium is null || codeLength <= SmallCodeSize ? _small
        : codeLength <= MediumCodeSize ? _medium
        : _large!;
}
