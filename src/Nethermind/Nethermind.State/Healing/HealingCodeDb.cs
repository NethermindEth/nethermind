// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Healing;

/// <summary>
/// Code database that fetches bytecode missing locally from the network and persists it.
/// </summary>
[method: DebuggerStepThrough]
public class HealingCodeDb(IKeyValueStoreWithBatching codeDb, Lazy<ICodeRecovery> recovery) : IKeyValueStoreWithBatching
{
    private readonly ConcurrentDictionary<ValueHash256, Lazy<Task<byte[]?>>> _inFlight = new();

    /// <inheritdoc cref="IReadOnlyKeyValueStore.Get(ReadOnlySpan{byte}, ReadFlags)"/>
    /// <remarks>
    /// On a miss, blocks while the bytecode is requested from peers, then writes it back so the next
    /// read is served locally. Only 32-byte keys are recoverable — anything else is a plain miss.
    /// </remarks>
    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        byte[]? bytes = codeDb.Get(key, flags);
        return bytes is null && TryRecover(key, out byte[]? recovered) ? recovered : bytes;
    }

    /// <inheritdoc cref="IReadOnlyKeyValueStore.GetSpan"/>
    /// <remarks>
    /// Recovered bytecode is persisted before the re-read, so every span handed out — and therefore
    /// every span given back to <see cref="DangerousReleaseMemory"/> — is owned by the wrapped store.
    /// A store that does not serve its own write back reads as a miss rather than healing here.
    /// </remarks>
    public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        Span<byte> span = codeDb.GetSpan(key, flags);
        return span.IsNull() && TryRecover(key, out _) ? codeDb.GetSpan(key, flags) : span;
    }

    /// <inheritdoc cref="IReadOnlyKeyValueStore.Get(ReadOnlySpan{byte}, Span{byte}, ReadFlags)"/>
    /// <remarks>
    /// A zero length cannot be told apart from a stored empty value, but code is keyed by its hash and
    /// empty code hashes to <see cref="Keccak.OfAnEmptyString"/>, which callers resolve without a read.
    /// </remarks>
    public int Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags = ReadFlags.None)
    {
        int length = codeDb.Get(key, output, flags);
        if (length == 0 && TryRecover(key, out byte[]? recovered))
        {
            recovered.CopyTo(output);
            return recovered.Length;
        }

        return length;
    }

    /// <inheritdoc cref="IReadOnlyKeyValueStore.GetOwnedMemory"/>
    public MemoryManager<byte>? GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        MemoryManager<byte>? memory = codeDb.GetOwnedMemory(key, flags);
        return memory is null && TryRecover(key, out byte[]? recovered) ? ArrayMemoryManager.From(recovered) : memory;
    }

    /// <inheritdoc cref="IReadOnlyKeyValueStore.KeyExists"/>
    /// <remarks>Recovers, so that existence answers the same question <see cref="Get(ReadOnlySpan{byte}, ReadFlags)"/> does.</remarks>
    public bool KeyExists(ReadOnlySpan<byte> key) => codeDb.KeyExists(key) || TryRecover(key, out _);

    /// <inheritdoc cref="IReadOnlyKeyValueStore.DangerousReleaseMemory"/>
    public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) => codeDb.DangerousReleaseMemory(span);

    /// <summary>Recovers the bytecode for <paramref name="key"/> from peers and persists it.</summary>
    /// <remarks>Blocks the calling thread for the whole recovery. Only 32-byte keys are code hashes.</remarks>
    private bool TryRecover(scoped ReadOnlySpan<byte> key, [NotNullWhen(true)] out byte[]? recovered)
    {
        recovered = key.Length == ValueHash256.MemorySize ? Recover(new ValueHash256(key)) : null;
        if (recovered is null) return false;

        Set(key, recovered);
        return true;
    }

    private byte[]? Recover(ValueHash256 codeHash)
    {
        // Each caller blocks a processing thread for the whole recovery, so concurrent misses share one
        // request. The Lazy keeps it to one even though GetOrAdd may run its factory more than once.
        Lazy<Task<byte[]?>> attempt = _inFlight.GetOrAdd(codeHash,
            hash => new Lazy<Task<byte[]?>>(() => recovery.Value.Recover(hash)));
        try
        {
            return attempt.Value.GetAwaiter().GetResult();
        }
        finally
        {
            // Match on the entry, not just the hash: a caller that resumes late would otherwise evict a
            // newer recovery that other callers are still sharing.
            _inFlight.TryRemove(new KeyValuePair<ValueHash256, Lazy<Task<byte[]?>>>(codeHash, attempt));
        }
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        codeDb.Set(key, value, flags);

    public IWriteBatch StartWriteBatch() => codeDb.StartWriteBatch();
}
