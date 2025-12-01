// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Paprika.Chain;
using IWorldState = Paprika.Chain.IWorldState;

namespace Nethermind.Paprika;

internal class PaprikaScope(
    IWorldState paprikaWorldState,
    IWorldStateScopeProvider.ICodeDb codeDb,
    SemaphoreSlim scopeLock,
    bool isReadOnly,
    global::Paprika.Chain.Blockchain paprikaBlockchain) : IWorldStateScopeProvider.IScope
{
    public void Dispose()
    {
        paprikaWorldState.Dispose();
        scopeLock.Release();
    }

    public Hash256 RootHash => paprikaWorldState.Hash.ToNethHash();

    public void UpdateRootHash()
    {
        // Paprika auto update on `Hash` getter
    }

    public Account? Get(Address address)
    {
        return paprikaWorldState.GetAccount(address.ToAccountPath.ToPaprikaKeccak()).ToNethAccount();
    }

    public void HintGet(Address address, Account? account)
    {
        // TODO: Probably not needed
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb => codeDb;
    public static long DebugBlockNumber { get; set; }

    public static bool Debug;

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        return new PaprikaStorageTree(paprikaWorldState, address.ToPaprikaKeccak());
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        return new PaprikaWriteBatcher(paprikaWorldState);
    }

    public void Commit(long blockNumber)
    {
        if (!isReadOnly)
        {
            paprikaWorldState.Commit((uint)blockNumber);

            if (blockNumber == 0)
            {
                paprikaBlockchain.Finalize(paprikaWorldState.Hash);
            }
        }
    }

    private class PaprikaStorageTree(IWorldState paprikaWorldState, global::Paprika.Crypto.Keccak address) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => Keccak.Zero;
        // public Hash256 RootHash => paprikaWorldState.GetAccount(address).StorageRootHash.ToNethHash();

        public byte[]? Get(in UInt256 index)
        {
            Span<byte> buffer = stackalloc byte[32];
            global::Paprika.Crypto.Keccak key = index.SlotToPaprikaKeccak();
            buffer = paprikaWorldState.GetStorage(address, key, buffer);
            return buffer.IsEmpty ? null : buffer.ToArray();
        }

        public void HintGet(in UInt256 index, byte[]? value)
        {
        }

        public byte[]? Get(in ValueHash256 hash)
        {
            return null;
        }
    }

    private class PaprikaWriteBatcher(IWorldState paprikaWorldState) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private Lock _writeLock = new Lock();

        public void Dispose()
        {
        }

#pragma warning disable CS0067
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated; // TODO: Umm....
#pragma warning restore CS0067
        public void Set(Address key, Account? account)
        {
            using var _ = _writeLock.EnterScope();
            var paprikaKeccak = key.ToPaprikaKeccak();
            if (account?.Nonce != 0)
            {
                Console.Error.WriteLine($"{PaprikaScope.DebugBlockNumber} Non zero nonce is {paprikaKeccak}:{key.ToAccountPath} {account}");
            }

            /*
            Paprika.Account originalAccount = paprikaWorldState.GetAccount(paprikaKeccak);

            // I dont know man.... I'm just trying things out.
            paprikaWorldState.SetAccount(key.ToPaprikaKeccak(),
                new Paprika.Account(
                    account?.Balance ?? originalAccount.Balance,
                    account?.Nonce ?? originalAccount.Nonce,
                    account?.CodeHash?.ToPaprikaKeccak() ?? originalAccount.CodeHash,
                    originalAccount.StorageRootHash)
            );
            */
            if (PaprikaScope.Debug)
            {
                Console.Error.WriteLine($"Set {paprikaKeccak} to {account}");
            }
            if (account == null)
            {
                paprikaWorldState.DestroyAccount(paprikaKeccak);
            }
            else
            {
                paprikaWorldState.SetAccount(paprikaKeccak, account.ToPaprikaAccount());
            }
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            return new PaprikaStorageWriter(
                paprikaWorldState.GetStorageSetter(key.ToPaprikaKeccak()),
                paprikaWorldState,
                key.ToPaprikaKeccak(),
                paprikaWorldState.GetAccount(key.ToPaprikaKeccak()).IsEmpty,
                _writeLock
            );
        }
    }

    private class PaprikaStorageWriter(
        IStorageSetter setter,
        IWorldState worldState,
        global::Paprika.Crypto.Keccak account,
#pragma warning disable CS9113 // Parameter is unread.
        bool originallyEmpty,
#pragma warning restore CS9113 // Parameter is unread.
        Lock writeLock
    ) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Dispose()
        {
        }

        public void Set(in UInt256 index, byte[] value)
        {
            if (PaprikaScope.Debug)
            {
                Console.Error.WriteLine($"Set {account}:{index} to {value?.ToHexString()}");
            }
            using var _ = writeLock.EnterScope();
            setter.SetStorage(index.SlotToPaprikaKeccak(), value);
        }

        public void Clear()
        {
            if (PaprikaScope.Debug)
            {
                Console.Error.WriteLine($"destroy account {account}");
            }
            using var _ = writeLock.EnterScope();
            worldState.DestroyAccount(account);
        }
    }
}
