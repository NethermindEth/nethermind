// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Caches built <see cref="InstructionStream"/>s separately from the CodeInfo cache, so a stream
/// survives CodeInfo eviction. Keyed by code hash alone is safe: the precharged op set is fork-invariant,
/// so a stream is valid for any fork &gt;= Shanghai (the only context it runs in). Cleared with the code
/// cache on a fork/state change. Bounded to <see cref="MemoryAllowance.InstructionStreamCacheSize"/> entries,
/// separately from the CodeInfo cache (per-entry footprint is far larger).
/// </summary>
internal static class InstructionStreamCache
{
    private static readonly AssociativeCache<ValueHash256, InstructionStream> _cache = new(MemoryAllowance.InstructionStreamCacheSize);

    public static bool TryGet(in ValueHash256 codeHash, out InstructionStream? stream) => _cache.TryGet(in codeHash, out stream);

    public static void Set(in ValueHash256 codeHash, InstructionStream stream) => _cache.Set(in codeHash, stream);

    public static void Clear() => _cache.Clear();
}
