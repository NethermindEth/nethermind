// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// No-op <see cref="IBlobArenaManager"/>. Useful for tests / synthetic
/// <see cref="PersistedSnapshot"/> instances that don't reference any blob arena
/// (so reads through <see cref="PersistedSnapshot.ResolveTrieRlp"/> are never
/// exercised). All Try* methods short-circuit so PersistedSnapshot.ctor sees
/// no leases to acquire.
/// </summary>
public sealed class NullBlobArenaManager : IBlobArenaManager
{
    public static readonly NullBlobArenaManager Instance = new();

    private NullBlobArenaManager() { }

    public void Initialize(IReadOnlyList<BlobArenaCatalog.Entry> allEntries) { }

    public BlobArenaWriter CreateWriter(long estimatedSize, string tag) =>
        throw new InvalidOperationException("NullBlobArenaManager cannot create writers.");

    public int RandomRead(ushort blobArenaId, long offset, Span<byte> destination) => 0;
    public bool TryLeaseFile(ushort blobArenaId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BlobArenaFile? file)
    {
        file = null;
        return false;
    }
    public void ReleaseBlobArena(ushort blobArenaId) { }
    public int BlobArenaFileCount => 0;
    public long BlobArenaMappedBytes => 0;
    public void Dispose() { }
}
