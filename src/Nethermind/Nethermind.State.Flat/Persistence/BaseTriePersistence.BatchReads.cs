// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public static partial class BaseTriePersistence
{
    public readonly partial struct Reader
    {
        public void LoadStateRlps(ReadOnlySpan<TreePath> paths, Span<byte[]?> values)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, paths.Length, nameof(values));
            for (int start = 0; start < paths.Length; start += BatchTriePersistenceReaderExtensions.BatchSize)
            {
                int count = Math.Min(BatchTriePersistenceReaderExtensions.BatchSize, paths.Length - start);
                ReadOnlySpan<TreePath> chunk = paths.Slice(start, count);
                Span<byte[]?> results = values.Slice(start, count);
                ReadRlpGroup(stateTopNodes, chunk, results, null, RlpKeyKind.StateTop);
                ReadRlpGroup(stateNodes, chunk, results, null, RlpKeyKind.State);
                ReadRlpGroup(fallbackNodes, chunk, results, null, RlpKeyKind.StateFallback);
            }
        }

        public void LoadStorageRlps(Hash256 address, ReadOnlySpan<TreePath> paths, Span<byte[]?> values)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, paths.Length, nameof(values));
            for (int start = 0; start < paths.Length; start += BatchTriePersistenceReaderExtensions.BatchSize)
            {
                int count = Math.Min(BatchTriePersistenceReaderExtensions.BatchSize, paths.Length - start);
                ReadOnlySpan<TreePath> chunk = paths.Slice(start, count);
                Span<byte[]?> results = values.Slice(start, count);
                ReadRlpGroup(storageNodes, chunk, results, address, RlpKeyKind.Storage);
                ReadRlpGroup(fallbackNodes, chunk, results, address, RlpKeyKind.StorageFallback);
            }
        }

        private static void ReadRlpGroup(IReadOnlyKeyValueStore store, ReadOnlySpan<TreePath> paths,
            Span<byte[]?> values, Hash256? address, RlpKeyKind kind)
        {
            Span<int> indexes = stackalloc int[BatchTriePersistenceReaderExtensions.BatchSize];
            Span<byte> keys = stackalloc byte[BatchTriePersistenceReaderExtensions.BatchSize * FullStorageNodesKeyLength];
            int count = 0;
            int keyLength = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                ref readonly TreePath path = ref paths[i];
                bool matches = kind switch
                {
                    RlpKeyKind.StateTop => path.Length <= StateNodesTopThreshold,
                    RlpKeyKind.State => path.Length > StateNodesTopThreshold && path.Length <= ShortenedPathThreshold,
                    RlpKeyKind.StateFallback or RlpKeyKind.StorageFallback => path.Length > ShortenedPathThreshold,
                    _ => path.Length <= ShortenedPathThreshold
                };
                if (!matches) continue;
                Span<byte> destination = keys[(count * keyLength)..];
                ReadOnlySpan<byte> encoded = kind switch
                {
                    RlpKeyKind.StateTop => EncodeStateTopNodeKey(destination, in path),
                    RlpKeyKind.State => EncodeShortenedStateNodeKey(destination, in path),
                    RlpKeyKind.StateFallback => EncodeFullStateNodeKey(destination, in path),
                    RlpKeyKind.Storage => EncodeShortenedStorageNodeKey(destination, address!, in path),
                    _ => EncodeFullStorageNodeKey(destination, address!, in path)
                };
                keyLength = encoded.Length;
                indexes[count++] = i;
            }
            if (count < BatchTriePersistenceReaderExtensions.MinBatchSize)
            {
                for (int i = 0; i < count; i++) values[indexes[i]] = store.Get(keys.Slice(i * keyLength, keyLength));
                return;
            }

            using ArrayPoolListRef<byte[]?> results = new(count, count);
            store.GetMany<byte[]?, OwnedRlpDecoder>(keys[..(count * keyLength)], keyLength, results.AsSpan(), default);
            for (int i = 0; i < count; i++) values[indexes[i]] = results[i];
        }

        private enum RlpKeyKind { StateTop, State, StateFallback, Storage, StorageFallback }

        private readonly struct OwnedRlpDecoder : IValueDecoder<byte[]?>
        {
            public byte[] Decode(ReadOnlySpan<byte> buffer) => buffer.ToArray();
        }
    }
}
