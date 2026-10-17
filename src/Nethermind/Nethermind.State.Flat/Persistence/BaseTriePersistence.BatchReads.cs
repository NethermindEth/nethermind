// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
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
                ReadRlpGroup(stateTopNodes, new UniformRlpSource(chunk, null), results, RlpKeyKind.StateTop);
                ReadRlpGroup(stateNodes, new UniformRlpSource(chunk, null), results, RlpKeyKind.State);
                ReadRlpGroup(fallbackNodes, new UniformRlpSource(chunk, null), results, RlpKeyKind.StateFallback);
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
                ReadRlpGroup(storageNodes, new UniformRlpSource(chunk, address), results, RlpKeyKind.Storage);
                ReadRlpGroup(fallbackNodes, new UniformRlpSource(chunk, address), results, RlpKeyKind.StorageFallback);
            }
        }

        public void LoadRlps(ReadOnlySpan<(Hash256? Address, TreePath Path)> requests, Span<byte[]?> values)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
            for (int start = 0; start < requests.Length; start += BatchTriePersistenceReaderExtensions.BatchSize)
            {
                int count = Math.Min(BatchTriePersistenceReaderExtensions.BatchSize, requests.Length - start);
                MixedRlpSource source = new(requests.Slice(start, count));
                Span<byte[]?> output = values.Slice(start, count);
                ReadRlpGroup(stateTopNodes, source, output, RlpKeyKind.StateTop);
                ReadRlpGroup(stateNodes, source, output, RlpKeyKind.State);
                ReadRlpGroup(storageNodes, source, output, RlpKeyKind.Storage);
                ReadRlpGroup(fallbackNodes, source, output, RlpKeyKind.StateFallback);
                ReadRlpGroup(fallbackNodes, source, output, RlpKeyKind.StorageFallback);
            }
        }

        [SkipLocalsInit]
        private static void ReadRlpGroup<TSource>(IReadOnlyKeyValueStore store, TSource source,
            Span<byte[]?> values, RlpKeyKind kind)
            where TSource : struct, IRlpSource, allows ref struct
        {
            int first = 0;
            while (first < source.Count && !MatchesGroup(source, first, kind)) first++;
            if (first == source.Count) return;

            int keyLength = kind switch
            {
                RlpKeyKind.StateTop => StateNodesTopPathLength,
                RlpKeyKind.State => ShortenedPathLength,
                RlpKeyKind.StateFallback => FullStateNodesKeyLength,
                RlpKeyKind.Storage => ShortenedStorageNodesKeyLength,
                _ => FullStorageNodesKeyLength
            };
            Span<int> indexes = stackalloc int[BatchTriePersistenceReaderExtensions.BatchSize];
            Span<byte> keys = stackalloc byte[source.Count * keyLength];
            int count = 0;
            for (int i = first; i < source.Count; i++)
            {
                if (!MatchesGroup(source, i, kind)) continue;
                ref readonly TreePath path = ref source.GetPath(i);
                Span<byte> destination = keys.Slice(count * keyLength, keyLength);
                _ = kind switch
                {
                    RlpKeyKind.StateTop => EncodeStateTopNodeKey(destination, in path),
                    RlpKeyKind.State => EncodeShortenedStateNodeKey(destination, in path),
                    RlpKeyKind.StateFallback => EncodeFullStateNodeKey(destination, in path),
                    RlpKeyKind.Storage => EncodeShortenedStorageNodeKey(destination, source.GetAddress(i)!, in path),
                    _ => EncodeFullStorageNodeKey(destination, source.GetAddress(i)!, in path)
                };
                indexes[count++] = i;
            }
            if (count < BatchTriePersistenceReaderExtensions.MinBatchSize)
            {
                for (int i = 0; i < count; i++) values[indexes[i]] = store.Get(keys.Slice(i * keyLength, keyLength));
                return;
            }
            if (count == source.Count)
            {
                store.GetMany<byte[]?, OwnedRlpDecoder>(keys[..(count * keyLength)], keyLength, values, default);
                return;
            }

            using ArrayPoolListRef<byte[]?> results = new(count, count);
            store.GetMany<byte[]?, OwnedRlpDecoder>(keys[..(count * keyLength)], keyLength, results.AsSpan(), default);
            for (int i = 0; i < count; i++) values[indexes[i]] = results[i];
        }

        private static bool MatchesGroup<TSource>(TSource source, int index, RlpKeyKind kind)
            where TSource : struct, IRlpSource, allows ref struct
        {
            bool storage = kind is RlpKeyKind.Storage or RlpKeyKind.StorageFallback;
            if ((source.GetAddress(index) is not null) != storage) return false;
            int length = source.GetPath(index).Length;
            return kind switch
            {
                RlpKeyKind.StateTop => length <= StateNodesTopThreshold,
                RlpKeyKind.State => length > StateNodesTopThreshold && length <= ShortenedPathThreshold,
                RlpKeyKind.StateFallback or RlpKeyKind.StorageFallback => length > ShortenedPathThreshold,
                _ => length <= ShortenedPathThreshold
            };
        }

        private interface IRlpSource
        {
            int Count { get; }
            ref readonly TreePath GetPath(int index);
            Hash256? GetAddress(int index);
        }

        private readonly ref struct UniformRlpSource(ReadOnlySpan<TreePath> paths, Hash256? address) : IRlpSource
        {
            private readonly ReadOnlySpan<TreePath> _paths = paths;
            public int Count => _paths.Length;
            public ref readonly TreePath GetPath(int index) => ref _paths[index];
            public Hash256? GetAddress(int index) => address;
        }

        private readonly ref struct MixedRlpSource(ReadOnlySpan<(Hash256? Address, TreePath Path)> requests) : IRlpSource
        {
            private readonly ReadOnlySpan<(Hash256? Address, TreePath Path)> _requests = requests;
            public int Count => _requests.Length;
            public ref readonly TreePath GetPath(int index) => ref _requests[index].Path;
            public Hash256? GetAddress(int index) => _requests[index].Address;
        }

        private enum RlpKeyKind { StateTop, State, StateFallback, Storage, StorageFallback }

        private readonly struct OwnedRlpDecoder : IValueDecoder<byte[]?>
        {
            public byte[] Decode(ReadOnlySpan<byte> buffer) => buffer.ToArray();
        }
    }
}
