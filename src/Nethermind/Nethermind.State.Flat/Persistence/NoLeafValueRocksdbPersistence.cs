// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

// this persistence remove the leaf data and rely on the flat db to reconstruct it. It means the trie db is smaller
// and probably faster but adds the latency of the flat during a leaf lookup.
public class NoLeafValueRocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    public NoLeafValueRocksdbPersistence(IColumnsDb<FlatDbColumns> db)
    {
        _db = db;
    }

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(CurrentStateKey);
        if (bytes is null || bytes.Length == 0)
        {
            return new StateId(-1, Keccak.EmptyTreeHash);
        }

        long blockNumber = BinaryPrimitives.ReadInt64BigEndian(bytes);
        Hash256 stateHash = new Hash256(bytes[8..]);
        return new StateId(blockNumber, stateHash);
    }

    internal static void SetCurrentState(IWriteOnlyKeyValueStore kv, StateId stateId)
    {
        Span<byte> bytes = stackalloc byte[8 + 32];
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], stateId.blockNumber);
        stateId.stateRoot.BytesAsSpan.CopyTo(bytes[8..]);

        kv.PutSpan(CurrentStateKey, bytes);
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        var snapshot = _db.CreateSnapshot();
        var currentState = ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

        ICacheOnlyReader state = (ICacheOnlyReader) snapshot.GetColumn(FlatDbColumns.Account);
        ICacheOnlyReader storage = (ICacheOnlyReader) snapshot.GetColumn(FlatDbColumns.Storage);

        var hashedFlatReader = new BaseFlatPersistence.Reader(
            state,
            storage
        );
        var flatReader = new BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>(hashedFlatReader);

        var trieReader = new TrieReader(
            snapshot.GetColumn(FlatDbColumns.StateTopNodes),
            snapshot.GetColumn(FlatDbColumns.StateNodes),
            snapshot.GetColumn(FlatDbColumns.StorageNodes),
            hashedFlatReader
        );

        return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>, TrieReader>(
            flatReader,
            trieReader,
            currentState,
            new Reactive.AnonymousDisposable(() =>
            {
                snapshot.Dispose();
            })
        );
    }

    public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to, WriteFlags flags)
    {
        var dbSnap = _db.CreateSnapshot();
        var currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        IWriteOnlyKeyValueStore state = batch.GetColumnBatch(FlatDbColumns.Account);
        IWriteOnlyKeyValueStore storage = batch.GetColumnBatch(FlatDbColumns.Storage);

        var flatWriter = new BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>(
            new BaseFlatPersistence.WriteBatch(
                ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage)),
                state,
                storage,
                flags
            )
        );

        var trieWriteBatch = new TrieWriter(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
            flags);

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, TrieWriter>(
            flatWriter,
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
                batch.Dispose();
                dbSnap.Dispose();
            })
        );
    }

    private struct TrieWriter(
        ISortedKeyValueStore storageNodesSnap,
        IWriteOnlyKeyValueStore stateTopNodes,
        IWriteOnlyKeyValueStore stateNodes,
        IWriteOnlyKeyValueStore storageNodes,
        WriteFlags flags
        ) : BasePersistence.ITrieWriteBatch
    {
        public void SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKey = stackalloc byte[BaseTriePersistence.StorageHashPrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[BaseTriePersistence.StorageNodesKeyLength];
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            accountPath.Bytes[..BaseTriePersistence.StorageHashPrefixLength].CopyTo(firstKey);
            accountPath.Bytes[..BaseTriePersistence.StorageHashPrefixLength].CopyTo(lastKey);

            int removedEntry = 0;
            using (ISortedView storageNodeReader = storageNodesSnap.GetViewBetween(firstKey, lastKey))
            {
                var storageNodeWriter = storageNodes;
                while (storageNodeReader.MoveNext())
                {
                    storageNodeWriter.Remove(storageNodeReader.CurrentKey);
                    removedEntry++;
                }
            }
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tn)
        {
            Span<byte> rlpSpan = tn.FullRlp.Span;

            if (tn.IsLeaf)
            {
                Span<byte> truncatedSpan = stackalloc byte[rlpSpan.Length];
                var rlpStream = new Rlp.ValueDecoderContext(rlpSpan);
                // The whole rlp
                rlpStream.ReadSequenceLength();

                int numberOfItems = rlpStream.PeekNumberOfItemsRemaining(null, 3);
                if (numberOfItems != 2) throw new InvalidOperationException($"Rlp of leaf should be exactly sequence of two. But got {numberOfItems}.");

                // Skip the key
                rlpStream.SkipItem();
                int offset;

                if (address is null)
                {
                    // Read the length of the value
                    (int prefixLength, int _) = rlpStream.PeekPrefixAndContentLength();

                    offset = rlpStream.Position + prefixLength;
                }
                else
                {
                    // For storage need to unwrap twice.
                    (int prefixLength, int contentLength) = rlpStream.PeekPrefixAndContentLength();
                    // If prefix length is 0 and content length is 1
                    // We dont want to skip anything.

                    // So the content itself is a bytearray of the actual value.
                    int byteArrayPrefix = 0;
                    if (contentLength > 0)
                    {
                        if (prefixLength > 0)
                        {
                            rlpStream.ReadByte();
                        }
                        if (rlpStream.Length == rlpStream.Position)
                        {
                            Console.Error.WriteLine($"The data is {tn.FullRlp.Span.ToHexString()}, to {prefixLength}, {contentLength}");
                        }
                        int prefix = rlpStream.PeekByte();
                        if (prefix < 128) // If its lower than this, then that is itself the value. So byteArrayPrefix is 0
                        {
                        }
                        else
                        {
                            // Then the value length is this byte - 128.
                            byteArrayPrefix = 1;

                            // Technically, it could also be more, but we dont support it as storage leaf value should not be more than 32
                        }
                    }

                    offset = rlpStream.Position + byteArrayPrefix;
                }

                rlpSpan[..offset].CopyTo(truncatedSpan);
                SetTrieNodesRaw(address, path, truncatedSpan[..offset]);
            }
            else
            {
                SetTrieNodesRaw(address, path, rlpSpan);
            }
        }

        private void SetTrieNodesRaw(Hash256? address, TreePath path, ReadOnlySpan<byte> rlpSpan)
        {
            if (address is null)
            {
                if (path.Length <= BaseTriePersistence.StateNodesTopThreshold)
                {
                    stateTopNodes.PutSpan(BaseTriePersistence.EncodeStateTopNodeKey(stackalloc byte[BaseTriePersistence.StateNodesTopKeyLength], path), rlpSpan, flags);
                }
                else
                {
                    stateNodes.PutSpan(BaseTriePersistence.EncodeStateNodeKey(stackalloc byte[BaseTriePersistence.StateNodesKeyLength], path), rlpSpan, flags);
                }
            }
            else
            {
                storageNodes.PutSpan(BaseTriePersistence.EncodeStorageNodeKey(stackalloc byte[BaseTriePersistence.StorageNodesKeyLength], address, path), rlpSpan, flags);
            }
        }

    }

    private readonly struct TrieReader(
        IReadOnlyKeyValueStore _stateNodes,
        IReadOnlyKeyValueStore _stateTopNodes,
        IReadOnlyKeyValueStore _storageNodes,
        BasePersistence.IHashedFlatReader flatReader
        ) : BasePersistence.ITrieReader
    {
        private const int AccountSpanBufferSize = 256;
        private const int SlotSpanBufferSize = 40;

        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
        {
            ReadOnlySpan<byte> rocksDbSpan = DoTryLoadRlp(address, path, hash, flags);
            try
            {
                if (rocksDbSpan.IsNullOrEmpty()) return null;

                var rlpStream = new Rlp.ValueDecoderContext(rocksDbSpan);
                rlpStream.ReadSequenceLength();
                int numberOfItems = rlpStream.PeekNumberOfItemsRemaining(null, 3);
                if (numberOfItems > 2) return rocksDbSpan.ToArray();

                ReadOnlySpan<byte> valueSpan = rlpStream.DecodeByteArraySpan();
                (byte[] key, bool isLeaf) = HexPrefix.FromBytes(valueSpan);
                if (!isLeaf) return rocksDbSpan.ToArray();

                ValueHash256 fullPath = path.Append(key).Path;
                if (address is null)
                {
                    Span<byte> buffer = stackalloc byte[AccountSpanBufferSize];
                    int readSize = flatReader.GetAccount(fullPath, buffer);

                    byte[] resultingValue = new byte[rocksDbSpan.Length + readSize];
                    rocksDbSpan.CopyTo(resultingValue);
                    buffer[..readSize].CopyTo(resultingValue.AsSpan(rocksDbSpan.Length));

                    return resultingValue;
                }
                else
                {
                    Span<byte> buffer = stackalloc byte[SlotSpanBufferSize];
                    int readSize = 0;
                    SlotValue slotValue = new SlotValue();
                    if (flatReader.TryGetStorage(address, fullPath, ref slotValue))
                    {
                        byte[] evmBytes = slotValue.ToEvmBytes();
                        readSize = evmBytes.Length;
                        evmBytes.CopyTo(buffer);
                    }
                    else
                    {
                        readSize = 1;
                        StorageTree.ZeroBytes.CopyTo(buffer);
                    }

                    byte[] resultingValue = new byte[rocksDbSpan.Length + readSize];
                    rocksDbSpan.CopyTo(resultingValue);
                    buffer[..readSize].CopyTo(resultingValue.AsSpan(rocksDbSpan.Length));

                    return resultingValue;
                }

                /*
                if (Keccak.Compute(resultingValue) != hash)
                {
                    byte[]? correctValue = DoTryLoadRlp(address, path.Append([1, 1, 1, 1, 1, 1, 1, 1, 1, 1]), hash, flags);
                    Console.Error.WriteLine($"Wrong concatenation {value.ToHexString()} + {leafValue.ToHexString()}.");
                    Console.Error.WriteLine($"Correct value is {correctValue.ToHexString()}.");
                    Console.Error.WriteLine($"Hash is {hash}");
                }
                */
            }
            finally
            {
                // Can this work with any DB?
                _storageNodes.DangerousReleaseMemory(rocksDbSpan);
            }
        }

        private ReadOnlySpan<byte> DoTryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
        {
            if (address is null)
            {
                if (path.Length <= BaseTriePersistence.StateNodesTopThreshold)
                {
                    return _stateTopNodes.GetSpan(BaseTriePersistence.EncodeStateTopNodeKey(stackalloc byte[BaseTriePersistence.StateNodesTopKeyLength], in path));
                }
                else
                {
                    return _stateNodes.GetSpan(BaseTriePersistence.EncodeStateNodeKey(stackalloc byte[BaseTriePersistence.StateNodesKeyLength], in path));
                }
            }
            else
            {
                return _storageNodes.GetSpan(BaseTriePersistence.EncodeStorageNodeKey(stackalloc byte[BaseTriePersistence.StorageNodesKeyLength], address, in path));
            }
        }
    }
}
