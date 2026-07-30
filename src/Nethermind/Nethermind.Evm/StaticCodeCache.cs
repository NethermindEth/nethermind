// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>Process-wide LRU <see cref="ICodeCache"/> used for normal block processing; the DI default and the shared cache tests reset between runs.</summary>
public sealed class StaticCodeCache : ICodeCache
{
    public static readonly StaticCodeCache Instance = new();

    private readonly AssociativeCache<ValueHash256, CodeInfo> _cache = new(MemoryAllowance.CodeCacheSize);

    public CodeInfo? Get(in ValueHash256 codeHash) => _cache.Get(in codeHash);

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        codeInfo.CodeHash = codeHash;
        _cache.Set(in codeHash, codeInfo);
    }

    public void Clear() => _cache.Clear();
}
