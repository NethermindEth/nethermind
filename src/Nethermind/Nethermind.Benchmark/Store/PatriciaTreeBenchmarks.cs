// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using Nethermind.Db.Blooms;

namespace Nethermind.Benchmarks.Store
{
    [MemoryDiagnoser]
    public class PatriciaTreeBenchmarks
    {
        private static readonly Account _empty = Build.An.Account.WithBalance(0).TestObject;
        private static readonly Account _account0 = Build.An.Account.WithBalance(1).TestObject;
        private static readonly Account _account1 = Build.An.Account.WithBalance(2).TestObject;
        private static readonly Account _account2 = Build.An.Account.WithBalance(3).TestObject;
        private static readonly Account _account3 = Build.An.Account.WithBalance(4).TestObject;

        private static readonly Keccak _keccak1 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000");
        private static readonly Keccak _keccak2 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0");
        private static readonly Keccak _keccak3 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1");
        private static readonly Keccak _keccak4 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111");
        private static readonly Keccak _keccak5 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd");
        private static readonly Keccak _keccak6 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd");
        private static readonly Keccak _keccak7 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000");
        private static readonly Keccak _keccak8 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111");
        private static readonly Keccak _keccak9 = new("1111111111111111111111111111111111111111111111111111111111111111");
        private static readonly Keccak _keccak10 = new("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd");
        private static readonly Keccak _keccak11 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111");
        private static readonly Keccak _keccak12 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000");
        private static readonly Keccak _keccak13 = new("111111111111111111111111111111111111111111111111111111111ddddddd");
        private static readonly Keccak _keccak14 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000");
        private static readonly Keccak _keccak15 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111");
        private static readonly Keccak _keccak16 = new("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222");

        private StateTree _tree;

        private readonly (string Name, Action<StateTree> Action)[] _scenarios = {
            ("set_3_via_address", tree =>
            {
                tree.Set(TestItem.AddressA, _account0);
                tree.Set(TestItem.AddressB, _account0);
                tree.Set(TestItem.AddressC, _account0);
                tree.Commit(1);
            }),
            ("set_3_via_hash", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak2, _account0);
                tree.Set(_keccak3, _account0);
                tree.Commit(1);
            }),
            ("set_3_delete_1", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak2, _account0);
                tree.Set(_keccak3, _account0);
                tree.Set(_keccak3, null);
                tree.Commit(1);
            }),
            ("set_3_delete_2", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak2, _account0);
                tree.Set(_keccak3, _account0);
                tree.Set(_keccak2, null);
                tree.Set(_keccak3, null);
                tree.Commit(1);
            }),
            ("set_3_delete_all", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak2, _account0);
                tree.Set(_keccak3, _account0);
                tree.Set(_keccak2, null);
                tree.Set(_keccak3, null);
                tree.Set(_keccak1, null);
                tree.Commit(1);
            }),
            ("extension_read_full_match", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak4, _account1);
                Account account = tree.Get(_keccak4);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_read_missing", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak4, _account1);
                Account account = tree.Get(_keccak5);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_new_branch", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak4, _account1);
                tree.Set(_keccak5, _account2);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_delete_missing", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak4, _account1);
                tree.Set(_keccak6, null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extenson_create_new_extension", tree =>
            {
                tree.Set(_keccak1, _account0);
                tree.Set(_keccak4, _account1);
                tree.Set(_keccak7, _account2);
                tree.Set(_keccak8, _account3);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_new_value", tree =>
            {
                tree.Set(_keccak9, _account0);
                tree.Set(_keccak9, _account1);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_no_change", tree =>
            {
                tree.Set(_keccak9, _account0);
                tree.Set(_keccak9, _account0);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_delete", tree =>
            {
                tree.Set(_keccak9, _account0);
                tree.Set(_keccak9, null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_delete_missing", tree =>
            {
                tree.Set(_keccak9, _account0);
                tree.Set(_keccak10, null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_update_extension", tree =>
            {
                tree.Set(_keccak11, _account0);
                tree.Set(_keccak12, _account1);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_read", tree =>
            {
                tree.Set(_keccak9, _account0);
                Account account = tree.Get(_keccak9);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_update_missing", tree =>
            {
                tree.Set(_keccak9, _account0);
                Account account = tree.Get(_keccak13);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_update_missing", tree =>
            {
                tree.Set(_keccak14, _account0);
                tree.Set(_keccak15, _account1);
                tree.Set(_keccak16, _account2);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_read_missing", tree =>
            {
                tree.Set(_keccak14, _account0);
                tree.Set(_keccak15, _account1);
                Account account = tree.Get(_keccak16);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_delete_missing", tree =>
            {
                tree.Set(_keccak14, _account0);
                tree.Set(_keccak15, _account1);
                tree.Set(_keccak16, null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
        };

        [GlobalSetup]
        public void Setup()
        {
            _tree = new StateTree();
        }

        [Benchmark]
        public void Current()
        {
            for (int i = 0; i < 19; i++)
            {
                _scenarios[i].Action(_tree);
            }
        }
    }
}
