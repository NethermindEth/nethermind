// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public interface IBatchTriePersistenceReader
{
    void LoadStateRlps(ReadOnlySpan<TreePath> paths, Span<byte[]?> values);
    void LoadStorageRlps(Hash256 address, ReadOnlySpan<TreePath> paths, Span<byte[]?> values);
}

public static class BatchTriePersistenceReaderExtensions
{
    internal const int BatchSize = PatriciaTree.WarmUpBatchSize;
    internal const int MinBatchSize = PatriciaTree.MinWarmUpBatchSize;

    public static void LoadStateRlps(this IPersistence.IPersistenceReader reader, ReadOnlySpan<TreePath> paths, Span<byte[]?> values)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, paths.Length, nameof(values));
        if (paths.Length >= MinBatchSize && reader is IBatchTriePersistenceReader batch)
        {
            batch.LoadStateRlps(paths, values);
            return;
        }
        for (int i = 0; i < paths.Length; i++) values[i] = reader.TryLoadStateRlp(in paths[i], ReadFlags.None);
    }

    public static void LoadStorageRlps(this IPersistence.IPersistenceReader reader, Hash256 address, ReadOnlySpan<TreePath> paths, Span<byte[]?> values)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, paths.Length, nameof(values));
        if (paths.Length >= MinBatchSize && reader is IBatchTriePersistenceReader batch)
        {
            batch.LoadStorageRlps(address, paths, values);
            return;
        }
        for (int i = 0; i < paths.Length; i++) values[i] = reader.TryLoadStorageRlp(address, in paths[i], ReadFlags.None);
    }
}
