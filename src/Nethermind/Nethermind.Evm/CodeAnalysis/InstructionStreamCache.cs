// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Caches built <see cref="InstructionStream"/>s by code hash, separately from the CodeInfo cache,
/// so a stream survives CodeInfo eviction instead of being rebuilt. Keyed by code hash alone: the
/// stream is fork-agnostic across the specialized dispatch fingerprints (Shanghai+ semantics), which
/// is the only context it runs in. Cleared together with the code cache on a fork/state change.
/// </summary>
internal static class InstructionStreamCache
{
    private static readonly AssociativeCache<ValueHash256, InstructionStream> _cache = new(MemoryAllowance.CodeCacheSize);

    public static bool TryGet(in ValueHash256 codeHash, out InstructionStream? stream) => _cache.TryGet(in codeHash, out stream);

    public static void Set(in ValueHash256 codeHash, InstructionStream stream) => _cache.Set(in codeHash, stream);

    public static void Clear() => _cache.Clear();
}
