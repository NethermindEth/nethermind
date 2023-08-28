// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Interfaces;

namespace Nethermind.Synchronization.Test;

public class VerkleTrieScenarios
{
    public static Account Empty;
    public static Account AccountJustState0;
    public static Account AccountJustState1;
    public static Account AccountJustState2;
    public static Account Account0;
    public static Account Account1;
    public static Account Account2;
    public static Account Account3;

    public static readonly byte[] Code0 = { 0, 0 };
    public static readonly byte[] Code1 = { 0, 1 };
    public static readonly byte[] Code2 = { 0, 2 };
    public static readonly byte[] Code3 = { 0, 3 };

    [MethodImpl(MethodImplOptions.Synchronized)]
    public static void InitOnce()
    {
        if (Empty is null)
        {
            Empty = Build.An.Account.WithBalance(0).TestObject;
            // these 4 accounts are considered to be with storage
            Account0 = Build.An.Account.WithBalance(1).WithCode(Code0).TestObject;
            Account1 = Build.An.Account.WithBalance(2).WithCode(Code1).TestObject;
            Account2 = Build.An.Account.WithBalance(3).WithCode(Code2).TestObject;
            Account3 = Build.An.Account.WithBalance(4).WithCode(Code3).TestObject;

            AccountJustState0 = Build.An.Account.WithBalance(1).TestObject;
            AccountJustState1 = Build.An.Account.WithBalance(2).TestObject;
            AccountJustState2 = Build.An.Account.WithBalance(3).TestObject;
        }
    }

    private static (string Name, Action<VerkleStateTree, IVerkleTrieStore, IDb> Action)[] _scenarios;

    public static (string Name, Action<VerkleStateTree, IVerkleTrieStore, IDb> Action)[] Scenarios
        => LazyInitializer.EnsureInitialized(ref _scenarios, InitScenarios);

    private static (string Name, Action<VerkleStateTree, IVerkleTrieStore, IDb> Action)[] InitScenarios()
    {
        return new (string, Action<VerkleStateTree, IVerkleTrieStore, IDb>)[]
        {
            ("empty", (tree, stateDb, codeDb) =>
            {
                tree.Commit();
                tree.CommitTree(0);
            }),
            ("set_3_via_address", (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                SetAccountWithStorageAndCode(tree, TestItem.AddressA, Account0, true);
                SetAccountWithStorageAndCode(tree, TestItem.AddressB, Account0, true);
                SetAccountWithStorageAndCode(tree, TestItem.AddressC, Account0, true);
                tree.Commit();
                tree.CommitTree(0);;
            }),
            ("branch_with_same_accounts_at_different_addresses", (tree, stateDb, codeDb) =>
            {
                codeDb[Keccak.Compute(Code0).Bytes] = Code0;
                Keccak account1 = new("1baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                Keccak account2 = new("2baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                tree.InsertStemBatch(account1.Bytes[..31], AccountJustState0.ToVerkleDict());
                tree.InsertStemBatch(account2.Bytes[..31], AccountJustState0.ToVerkleDict());
                tree.Commit();
                tree.CommitTree(0);
            }),
            ("just_state", (tree, stateDb, codeDb) =>
            {
                Keccak account1 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000");
                Keccak account2 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111");
                Keccak account3 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd");
                tree.InsertStemBatch(account1.Bytes[..31], AccountJustState0.ToVerkleDict());
                tree.InsertStemBatch(account2.Bytes[..31], AccountJustState1.ToVerkleDict());
                tree.InsertStemBatch(account3.Bytes[..31], AccountJustState2.ToVerkleDict());
                tree.Commit();
                tree.CommitTree(0);
            })
        };
    }

    private static void SetAccountWithStorageAndCode(VerkleStateTree tree, Address address, Account account,
        bool setStorage = false)

    {
        tree.Set(address, account);
        if (account.Code is not null) tree.SetCode(address, account.Code);

        if(!setStorage) return;
        tree.SetStorage(new StorageCell(address, 1), new byte[] { 1 });
        tree.SetStorage(new StorageCell(address, 2), new byte[] { 2 });
        tree.SetStorage(new StorageCell(address, 3), new byte[] { 3 });
        tree.SetStorage(new StorageCell(address, 4), new byte[] { 4 });
        tree.SetStorage(new StorageCell(address, 1005), new byte[] { 5 });
        tree.SetStorage(new StorageCell(address, 1006), new byte[] { 6 });
        tree.SetStorage(new StorageCell(address, 1007), new byte[] { 7 });
        tree.SetStorage(new StorageCell(address, 1008), new byte[] { 8 });
    }

}
