// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// A collection of classes to make combining persistence code easier.
/// Implementation accept generic of dependencies and the dependencies must be struct. This allow better inlining and devirtualized calls.
///
/// <see cref="IPersistence"/> implementation is expected to create <see cref="Reader{TFlatReader,TTrieReader}"/> and
/// <see cref="WriteBatch{TFlatWriteBatch,TTrieWriteBatch}"/> passing in the flat and trie implementations along with
/// some dispose logic.
///
/// Flat implementation is largely expected to implement <see cref="IHashedFlatReader"/> and <see cref="IHashedFlatWriteBatch"/>
/// which then get wrapped into <see cref="IFlatReader"/> and <see cref="IFlatWriteBatch"/> with <see cref="ToHashedFlatReader{TFlatReader}"/>
/// and <see cref="ToHashedWriteBatch{TWriteBatch}"/>. This allow preimage variation which does not hash the keys.
/// </summary>
public static class BasePersistence
{
    public interface IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer);
        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref SlotValue outValue);
    }

    public interface IHashedFlatWriteBatch
    {
        public int SelfDestruct(in ValueHash256 address);

        public void RemoveAccount(in ValueHash256 address);

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value);

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, in SlotValue? value);
    }

    public interface IFlatReader
    {
        public Account? GetAccount(Address address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);
        public byte[]? GetAccountRaw(Hash256 addrHash);
        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue);
    }

    public interface IFlatWriteBatch
    {
        public int SelfDestruct(Address addr);

        public void RemoveAccount(Address addr);

        public void SetAccount(Address addr, Account account);

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value);

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value);

        public void SetAccountRaw(Hash256 addrHash, Account account);
    }

    public interface ITrieReader
    {
        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags);
    }

    public interface ITrieWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);
        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tnValue);
    }

    public readonly struct ToHashedWriteBatch<TWriteBatch>(
        TWriteBatch flatWriteBatch
    ) : IFlatWriteBatch
        where TWriteBatch : struct, IHashedFlatWriteBatch
    {
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Slim;

        public int SelfDestruct(Address addr)
        {
            ValueHash256 accountPath = addr.ToAccountPath;
            return flatWriteBatch.SelfDestruct(accountPath);
        }

        public void RemoveAccount(Address addr)
        {
            flatWriteBatch.RemoveAccount(addr.ToAccountPath);
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            flatWriteBatch.SetAccount(addr.ToAccountPath, stream.AsSpan());
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            ValueHash256 hashBuffer = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, hashBuffer.BytesAsSpan);
            flatWriteBatch.SetStorage(addr.ToAccountPath, hashBuffer, value);
        }

        public void SetStorageRaw(Hash256? addrHash, Hash256 slotHash, in SlotValue? value)
        {
            flatWriteBatch.SetStorage(addrHash, slotHash, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            flatWriteBatch.SetAccount(addrHash, stream.AsSpan());
        }
    }

    public readonly struct ToHashedFlatReader<TFlatReader>(
        TFlatReader flatReader
    ) : IFlatReader
    where TFlatReader : struct, IHashedFlatReader
    {
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Slim;
        private readonly int _accountSpanBufferSize = 256;

        public Account? GetAccount(Address address)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = flatReader.GetAccount(address.ToAccountPath, valueBuffer);
            if (responseSize == 0)
            {
                return null;
            }

            var ctx = new Rlp.ValueDecoderContext(valueBuffer[..responseSize]);
            return _accountDecoder.Decode(ref ctx);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, slotHash.BytesAsSpan);

            return TryGetSlotRaw(address.ToAccountPath, slotHash, ref outValue);
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = flatReader.GetAccount(addrHash.ValueHash256, valueBuffer);
            if (responseSize == 0) return null;
            return valueBuffer[..responseSize].ToArray();
        }

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue)
        {
            return flatReader.TryGetStorage(address, slotHash, ref outValue);
        }
    }

    public class Reader<TFlatReader, TTrieReader> : IPersistence.IPersistenceReader
        where TFlatReader : struct, IFlatReader
        where TTrieReader : struct, ITrieReader
    {
        private readonly TTrieReader _trieReader;
        private readonly TFlatReader _flatReader;
        private readonly IDisposable _disposer;

        public Reader(TFlatReader flatReader, TTrieReader trieReader, StateId currentState, IDisposable disposer)
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

        public Account? GetAccount(Address address)
        {
            return _flatReader.GetAccount(address);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            return _flatReader.TryGetSlot(address, in slot, ref outValue);
        }

        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
        {
            return _trieReader.TryLoadRlp(address, path, hash, flags);
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            return _flatReader.GetAccountRaw(addrHash);
        }

        public byte[]? GetStorageRaw(Hash256 addrHash, Hash256 slotHash)
        {
            SlotValue slotValue = new SlotValue();
            _flatReader.TryGetSlotRaw(addrHash, slotHash, ref slotValue);
            return slotValue.ToEvmBytes();
        }
    }

    public class WriteBatch<TFlatWriteBatch, TTrieWriteBatch>(
        in TFlatWriteBatch flatWriteBatch,
        TTrieWriteBatch trieWriteBatch,
        IDisposable disposer)
        : IPersistence.IWriteBatch
        where TFlatWriteBatch : struct, IFlatWriteBatch
        where TTrieWriteBatch : struct, ITrieWriteBatch
    {
        private readonly TFlatWriteBatch _flatWriter = flatWriteBatch;
        private readonly TTrieWriteBatch _trieWriteBatch = trieWriteBatch;

        public void Dispose()
        {
            disposer.Dispose();
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

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            _flatWriter.SetStorage(addr, slot, value);
        }

        public void SetTrieNodes(Hash256? address, in TreePath path, TrieNode tnValue)
        {
            _trieWriteBatch.SetTrieNodes(address, path, tnValue);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value)
        {
            _flatWriter.SetStorageRaw(addrHash, slotHash, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            _flatWriter.SetAccountRaw(addrHash, account);
        }
    }
}
