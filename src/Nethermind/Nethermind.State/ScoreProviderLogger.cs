// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class ScoreProviderLogger(IWorldStateScopeProvider.IScope baseScope): IWorldStateScopeProvider.IScope
{
    public void Dispose()
    {
        baseScope.Dispose();
    }

    public Hash256 RootHash => baseScope.RootHash;

    public void UpdateRootHash()
    {
        baseScope.UpdateRootHash();
        Console.Error.WriteLine($"Update root to {RootHash}");
    }

    public Account? Get(Address address)
    {
        return baseScope.Get(address);
    }

    public void HintGet(Address address, Account? account)
    {
        baseScope.HintGet(address, account);
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        return new StorageTreeLogger(baseScope.CreateStorageTree(address));
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        return new WorldStateWriteLogger(baseScope.StartWriteBatch(estimatedAccountNum));
    }

    public void Commit(long blockNumber)
    {
        baseScope.Commit(blockNumber);
        Console.Error.WriteLine($"Commit {blockNumber} to {RootHash}");
    }

    public class StorageTreeLogger(IWorldStateScopeProvider.IStorageTree baseStorageTree)
        : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[] Get(in UInt256 index)
        {
            return baseStorageTree.Get(in index);
        }

        public void HintGet(in UInt256 index, byte[]? value)
        {
            baseStorageTree.HintGet(in index, value);
        }

        public byte[] Get(in ValueHash256 hash)
        {
            return baseStorageTree.Get(in hash);
        }
    }

    public class WorldStateWriteLogger(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        public void Dispose()
        {
            writeBatch.Dispose();
        }

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => writeBatch.OnAccountUpdated += value;
            remove => writeBatch.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account)
        {
            Console.Error.WriteLine($"Set key {key.ToAccountPath}, {account}");
            writeBatch.Set(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            return new StorageWriteBatch(writeBatch.CreateStorageWriteBatch(key, estimatedEntries), key);
        }
    }

    public class StorageWriteBatch(IWorldStateScopeProvider.IStorageWriteBatch writeBatch, Address addr) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Dispose()
        {
            writeBatch.Dispose();
        }

        public void Set(in UInt256 index, byte[] value)
        {
            Console.Error.WriteLine($"Set storage key {addr}: {index}, {value?.ToHexString()}");
            writeBatch.Set(in index, value);
        }

        public void Clear()
        {
            Console.Error.WriteLine($"Clear {addr}");
            writeBatch.Clear();
        }
    }
}
