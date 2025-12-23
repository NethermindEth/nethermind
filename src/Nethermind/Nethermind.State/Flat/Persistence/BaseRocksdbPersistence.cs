// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Net.NetworkInformation;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
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
