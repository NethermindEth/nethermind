// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.State;

public class KeyValueWithBatchingBackedCodeDb(IKeyValueStoreWithBatching codeDb, bool isPersistent = false) : IWorldStateScopeProvider.ICodeDb
{
    // Persisted-code hint cache. Non-null only for durable codeDbs (production).
    // Overlay codeDbs leave this null — overlay writes are not durable and must never
    // populate a hint that survives the overlay's reset.
    // Capacity 1_024: 4x the per-block filter (256) to cover hot factory-deployed
    // bytecode across multiple recent blocks. False negatives just cause a redundant
    // write; false positives would lose the just-deployed code (the bug being prevented).
    private readonly AssociativeKeyCache<ValueHash256>? _persistedHint
        = isPersistent ? new AssociativeKeyCache<ValueHash256>(1_024) : null;

    /// <remarks>
    /// Reads a native store's slice straight into executable code memory, the one copy a cache-busting block
    /// pays per load. Loaded code is cached above as CodeInfo, so a block-cache copy is redundant; a block of
    /// distinct 64 KiB contracts would otherwise evict on every read.
    /// </remarks>
    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
    {
        if (codeDb is not IReadOnlyNativeKeyValueStore native) return codeDb.Get(codeHash.Bytes, ReadFlags.HintCacheMiss);

        ReadOnlySpan<byte> slice = native.GetNativeSlice(codeHash.Bytes, out nint handle, ReadFlags.HintCacheMiss);
        try
        {
            return slice.IsNull() ? default : ExecutableCodeMemory.Copy(slice);
        }
        finally
        {
            if (handle != 0) native.DangerousReleaseHandle(handle);
        }
    }

    public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new CodeSetter(codeDb.StartWriteBatch());

    public bool ContainsCode(in ValueHash256 codeHash) => _persistedHint?.Get(codeHash) ?? false;

    public void MarkCodePersisted(in ValueHash256 codeHash) => _persistedHint?.Set(codeHash);

    private class CodeSetter(IWriteBatch writeBatch) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) => writeBatch.PutSpan(codeHash.Bytes, code);

        public void Dispose() => writeBatch.Dispose();
    }
}
