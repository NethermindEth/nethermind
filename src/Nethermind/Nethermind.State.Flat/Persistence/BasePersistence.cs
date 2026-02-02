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
    public const int StoragePrefixPortion = 4;

    internal static void CreateStorageRange(
        ReadOnlySpan<byte> accountPath,
        Span<byte> firstKey,
        Span<byte> lastKey)
    {
        accountPath[..StoragePrefixPortion].CopyTo(firstKey);
        accountPath[..StoragePrefixPortion].CopyTo(lastKey);
        firstKey[StoragePrefixPortion..].Clear();
        lastKey[StoragePrefixPortion..].Fill(0xff);
    }

    public interface IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer);
        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref SlotValue outValue);
        public IPersistence.IFlatIterator CreateAccountIterator();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey);
        public bool IsPreimageMode { get; }
    }

    public interface IHashedFlatWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);

        public void RemoveAccount(in ValueHash256 address);

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value);

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, in SlotValue? value);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath);
    }

    public interface IFlatReader
    {
        public Account? GetAccount(Address address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue);
        public byte[]? GetAccountRaw(Hash256 addrHash);
        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue);
        public IPersistence.IFlatIterator CreateAccountIterator();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey);
        public bool IsPreimageMode { get; }
    }

    public interface IFlatWriteBatch
    {
        public void SelfDestruct(Address addr);

        public void SetAccount(Address addr, Account? account);

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value);

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value);

        public void SetAccountRaw(Hash256 addrHash, Account account);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath);
    }

    public interface ITrieReader
    {
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags);
    }

    public interface ITrieWriteBatch
    {
        public void SelfDestruct(in ValueHash256 address);
        public void SetStateTrieNode(in TreePath path, TrieNode tnValue);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue);
        public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath);
    }

    public struct ToHashedWriteBatch<TWriteBatch>(
        TWriteBatch flatWriteBatch,
        bool useFlatAccount = true
    ) : IFlatWriteBatch
        where TWriteBatch : struct, IHashedFlatWriteBatch
    {
        private readonly AccountDecoder _accountDecoder = useFlatAccount ? AccountDecoder.Slim : AccountDecoder.Instance;
        private TWriteBatch _flatWriteBatch = flatWriteBatch;

        public void SelfDestruct(Address addr) => _flatWriteBatch.SelfDestruct(addr.ToAccountPath);

        public void SetAccount(Address addr, Account? account)
        {
            if (account is null)
            {
                _flatWriteBatch.RemoveAccount(addr.ToAccountPath);
                return;
            }

            using NettyRlpStream stream = _accountDecoder.EncodeToNewNettyStream(account);
            _flatWriteBatch.SetAccount(addr.ToAccountPath, stream.AsSpan());
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            ValueHash256 hashBuffer = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref hashBuffer);
            _flatWriteBatch.SetStorage(addr.ToAccountPath, hashBuffer, value);
        }

        public void SetStorageRaw(Hash256? addrHash, Hash256 slotHash, in SlotValue? value) =>
            _flatWriteBatch.SetStorage(addrHash, slotHash, value);

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            using NettyRlpStream stream = _accountDecoder.EncodeToNewNettyStream(account);
            _flatWriteBatch.SetAccount(addrHash, stream.AsSpan());
        }

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriteBatch.DeleteAccountRange(fromPath, toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriteBatch.DeleteStorageRange(addressHash, fromPath, toPath);
    }

    public struct ToHashedFlatReader<TFlatReader>(
        TFlatReader flatReader,
        bool useFlatAccount = true
    ) : IFlatReader
        where TFlatReader : struct, IHashedFlatReader
    {
        private readonly AccountDecoder _accountDecoder = useFlatAccount ? AccountDecoder.Slim : AccountDecoder.Instance;
        private readonly int _accountSpanBufferSize = 256;
        private TFlatReader _flatReader = flatReader;

        public Account? GetAccount(Address address)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = _flatReader.GetAccount(address.ToAccountPath, valueBuffer);
            if (responseSize == 0)
            {
                return null;
            }

            Rlp.ValueDecoderContext ctx = new(valueBuffer[..responseSize]);
            return _accountDecoder.Decode(ref ctx);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);

            return TryGetSlotRaw(address.ToAccountPath, slotHash, ref outValue);
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = _flatReader.GetAccount(addrHash.ValueHash256, valueBuffer);
            return responseSize == 0 ? null : valueBuffer[..responseSize].ToArray();
        }

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue) =>
            _flatReader.TryGetStorage(address, slotHash, ref outValue);

        public IPersistence.IFlatIterator CreateAccountIterator() =>
            _flatReader.CreateAccountIterator();

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey) =>
            _flatReader.CreateStorageIterator(accountKey);

        public bool IsPreimageMode => _flatReader.IsPreimageMode;
    }

    public class Reader<TFlatReader, TTrieReader>(
        TFlatReader flatReader,
        TTrieReader trieReader,
        StateId currentState,
        IDisposable disposer)
        : IPersistence.IPersistenceReader
        where TFlatReader : struct, IFlatReader
        where TTrieReader : struct, ITrieReader
    {
        private TTrieReader _trieReader = trieReader;
        private TFlatReader _flatReader = flatReader;

        public StateId CurrentState { get; } = currentState;

        public void Dispose() => disposer.Dispose();

        public Account? GetAccount(Address address) =>
            _flatReader.GetAccount(address);

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) =>
            _flatReader.TryGetSlot(address, in slot, ref outValue);

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            _trieReader.TryLoadStateRlp(path, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            _trieReader.TryLoadStorageRlp(address, path, flags);

        public byte[]? GetAccountRaw(Hash256 addrHash) =>
            _flatReader.GetAccountRaw(addrHash);

        public bool TryGetStorageRaw(Hash256 addrHash, Hash256 slotHash, ref SlotValue value) =>
            _flatReader.TryGetSlotRaw(addrHash, slotHash, ref value);

        public IPersistence.IFlatIterator CreateAccountIterator() =>
            _flatReader.CreateAccountIterator();

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey) =>
            _flatReader.CreateStorageIterator(accountKey);

        public bool IsPreimageMode => _flatReader.IsPreimageMode;
    }

    public class WriteBatch<TFlatWriteBatch, TTrieWriteBatch>(
        in TFlatWriteBatch flatWriteBatch,
        TTrieWriteBatch trieWriteBatch,
        IDisposable disposer)
        : IPersistence.IWriteBatch
        where TFlatWriteBatch : struct, IFlatWriteBatch
        where TTrieWriteBatch : struct, ITrieWriteBatch
    {
        private TFlatWriteBatch _flatWriter = flatWriteBatch;
        private TTrieWriteBatch _trieWriteBatch = trieWriteBatch;

        public void Dispose() => disposer.Dispose();

        public void SelfDestruct(Address addr)
        {
            _flatWriter.SelfDestruct(addr);
            _trieWriteBatch.SelfDestruct(addr.ToAccountPath);
        }

        public void SetAccount(Address addr, Account? account) =>
            _flatWriter.SetAccount(addr, account);

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) =>
            _flatWriter.SetStorage(addr, slot, value);

        public void SetStateTrieNode(in TreePath path, TrieNode tnValue) =>
            _trieWriteBatch.SetStateTrieNode(path, tnValue);

        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue) =>
            _trieWriteBatch.SetStorageTrieNode(address, path, tnValue);

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value) =>
            _flatWriter.SetStorageRaw(addrHash, slotHash, value);

        public void SetAccountRaw(Hash256 addrHash, Account account) =>
            _flatWriter.SetAccountRaw(addrHash, account);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriter.DeleteAccountRange(fromPath, toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _flatWriter.DeleteStorageRange(addressHash, fromPath, toPath);

        public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) =>
            _trieWriteBatch.DeleteStateTrieNodeRange(fromPath, toPath);

        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) =>
            _trieWriteBatch.DeleteStorageTrieNodeRange(addressHash, fromPath, toPath);
    }
}
