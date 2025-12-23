// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class BaseRocksdbPersistence
{
    public interface IFlatReader
    {
        public bool TryGetAccount(Address address, out Account? acc);
        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes);
        public byte[]? GetAccountRaw(Hash256 addrHash);
        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash);
    }

    public interface IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer);
        public int GetStorage(in ValueHash256 address, in ValueHash256 slot, Span<byte> outBuffer);
    }

    public interface ITrieReader
    {
        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags);
    }

    public interface IFlatWriteBatch
    {
        public int SelfDestruct(Address addr);

        public void RemoveAccount(Address addr);

        public void SetAccount(Address addr, Account account);

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value);

        public void RemoveStorage(Address addr, UInt256 slot);

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value);

        public void SetAccountRaw(Hash256 addrHash, Account account);
    }

    public interface IHashedFlatWriteBatch
    {
        public int SelfDestruct(in ValueHash256 address);

        public void RemoveAccount(in ValueHash256 address);

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value);

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, ReadOnlySpan<byte> value);

        public void RemoveStorage(in ValueHash256 address, in ValueHash256 slotHash);
    }

    public interface ITrieWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);
        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tnValue);
    }

    public class WriteBatch<TFlatWriteBatch, TTrieWriteBatch> : IPersistence.IWriteBatch
        where TFlatWriteBatch : struct, IFlatWriteBatch
        where TTrieWriteBatch : struct, ITrieWriteBatch
    {
        private TFlatWriteBatch _flatWriter;
        private TTrieWriteBatch _trieWriteBatch;
        private readonly IDisposable _disposer;

        public WriteBatch(
            TFlatWriteBatch flatWriteBatch,
            TTrieWriteBatch trieWriteBatch,
            IDisposable disposer)
        {
            _flatWriter = flatWriteBatch;
            _trieWriteBatch = trieWriteBatch;
            _disposer = disposer;
        }

        public void Dispose()
        {
            _disposer.Dispose();
        }

        public int SelfDestruct(Address addr)
        {
            int removed = _flatWriter.SelfDestruct(addr);
            _trieWriteBatch.SelfDestruct(addr.ToAccountPath);
            return removed;
        }

        public void RemoveAccount(Address addr)
        {
            _flatWriter.RemoveAccount(addr);
        }

        public void SetAccount(Address addr, Account account)
        {
            _flatWriter.SetAccount(addr, account);
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            _flatWriter.SetStorage(addr, slot, value);
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tnValue)
        {
            _trieWriteBatch.SetTrieNodes(address, path, tnValue);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            _flatWriter.RemoveStorage(addr, slot);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            _flatWriter.SetStorageRaw(addrHash, slotHash, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            _flatWriter.SetAccountRaw(addrHash, account);
        }
    }

    public struct ToHashedWriteBatch<TWriteBatch>(
        TWriteBatch flatWriteBatch
    ) : IFlatWriteBatch
        where TWriteBatch : struct, IHashedFlatWriteBatch
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Instance;
        private TWriteBatch _flatWriteBatch = flatWriteBatch;

        public int SelfDestruct(Address addr)
        {
            ValueHash256 accountPath = addr.ToAccountPath;
            return _flatWriteBatch.SelfDestruct(accountPath);
        }

        public void RemoveAccount(Address addr)
        {
            _flatWriteBatch.RemoveAccount(addr.ToAccountPath);
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            _flatWriteBatch.SetAccount(addr.ToAccountPath, stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ValueHash256 hashBuffer = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, hashBuffer.BytesAsSpan);
            _flatWriteBatch.SetStorage(addr.ToAccountPath, hashBuffer, value);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ValueHash256 hashBuffer = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, hashBuffer.BytesAsSpan);
            _flatWriteBatch.RemoveStorage(addr.ToAccountPath, hashBuffer);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            _flatWriteBatch.SetStorage(addrHash, slotHash, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            _flatWriteBatch.SetAccount(addrHash, stream.AsSpan());
        }
    }

    public struct ToHashedFlatReader<TFlatReader>(
        TFlatReader flatReader
    ) : IFlatReader
    where TFlatReader : struct, IHashedFlatReader
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Instance;
        private int _accountSpanBufferSize = 256;
        private int _slotSpanBufferSize = 40;
        public bool TryGetAccount(Address address, out Account? acc)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = flatReader.GetAccount(address.ToAccountPath, valueBuffer);
            if (responseSize == 0)
            {
                acc = null;
                return false;
            }

            var ctx = new Rlp.ValueDecoderContext(valueBuffer[..responseSize]);
            acc = _accountDecoder.Decode(ref ctx);
            return true;
        }

        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(index, slotHash.BytesAsSpan);

            Span<byte> valueBuffer = stackalloc byte[_slotSpanBufferSize];
            int responseSize = flatReader.GetStorage(address.ToAccountPath, slotHash, valueBuffer);
            if (responseSize == 0)
            {
                valueBytes = null;
                return false;
            }

            valueBytes = valueBuffer[..responseSize].ToArray();
            return true;
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = flatReader.GetAccount(addrHash.ValueHash256, valueBuffer);
            if (responseSize == 0) return null;
            return valueBuffer[..responseSize].ToArray();
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            Span<byte> valueBuffer = stackalloc byte[_slotSpanBufferSize];
            int responseSize = flatReader.GetStorage(addrHash.ValueHash256, slotHash.ValueHash256, valueBuffer);
            if (responseSize == 0) return null;
            return valueBuffer[..responseSize].ToArray();
        }
    }


    public class PersistenceReader<TFlatReader, TTrieReader> : IPersistence.IPersistenceReader
        where TFlatReader : struct, IFlatReader
        where TTrieReader : struct, ITrieReader
    {
        private readonly TTrieReader _trieReader;
        private readonly TFlatReader _flatReader;
        private readonly IDisposable _disposer;

        public PersistenceReader(TFlatReader flatReader, TTrieReader trieReader, StateId currentState, IDisposable disposer)
        {
            _flatReader = flatReader;
            _trieReader = trieReader;
            _disposer = disposer;

            CurrentState = currentState;
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
            _disposer.Dispose();
        }

        public bool TryGetAccount(Address address, out Account? acc)
        {
            return _flatReader.TryGetAccount(address, out acc);
        }

        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            return _flatReader.TryGetSlot(address, in index, out valueBytes);
        }

        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
        {
            return _trieReader.TryLoadRlp(address, path, hash, flags);
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            return _flatReader.GetAccountRaw(addrHash);
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            return _flatReader.GetStorageRaw(addrHash, slotHash);
        }
    }
}
