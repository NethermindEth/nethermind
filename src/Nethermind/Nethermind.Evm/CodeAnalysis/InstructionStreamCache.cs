// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Caches built <see cref="InstructionStream"/>s separately from the CodeInfo cache, so a stream
/// survives CodeInfo eviction. Keyed by code hash alone is safe: the stream is fork-agnostic across
/// the specialized dispatch fingerprints (Shanghai+), the only context it runs in. Cleared with the
/// code cache on a fork/state change.
/// </summary>
internal static class InstructionStreamCache
{
    private static readonly AssociativeCache<ValueHash256, InstructionStream> _cache = new(MemoryAllowance.CodeCacheSize);

    public static bool TryGet(in ValueHash256 codeHash, out InstructionStream? stream) => _cache.TryGet(in codeHash, out stream);

    public static void Set(in ValueHash256 codeHash, InstructionStream stream) => _cache.Set(in codeHash, stream);

    public static void Clear() => _cache.Clear();
}
