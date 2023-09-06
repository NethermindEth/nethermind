// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store
{
    public class PatriciaTreeBenchmarks
    {
        private static readonly Account _empty = Build.An.Account.WithBalance(0).TestObject;
        private static readonly Account _account0 = Build.An.Account.WithBalance(1).TestObject;
        private static readonly Account _account1 = Build.An.Account.WithBalance(2).TestObject;
        private static readonly Account _account2 = Build.An.Account.WithBalance(3).TestObject;
        private static readonly Account _account3 = Build.An.Account.WithBalance(4).TestObject;

        private StateTree _tree;

        private (string Name, Action<StateTree> Action)[] _scenarios = new (string, Action<StateTree>)[]
        {
            ("set_3_via_address", tree =>
            {
                tree.Set(TestItem.AddressA, _account0);
                tree.Set(TestItem.AddressB, _account0);
                tree.Set(TestItem.AddressC, _account0);
                tree.Commit(1);
            }),
            ("set_3_via_hash", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Commit(1);
            }),
            ("set_3_delete_1", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit(1);
            }),
            ("set_3_delete_2", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit(1);
            }),
            ("set_3_delete_all", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
                tree.Commit(1);
            }),
            ("extension_read_full_match", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_read_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_new_branch", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_delete_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extenson_create_new_extension", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_new_value", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_no_change", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_delete", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_delete_missing", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_update_extension", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_read", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_update_missing", tree =>
            {
                tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Keccak("111111111111111111111111111111111111111111111111111111111ddddddd"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_update_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_read_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
                tree.UpdateRootHash();
                Keccak rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_delete_missing", tree =>
            {
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
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
        public void Improved()
        {
            for (int i = 0; i < 19; i++)
            {
                _scenarios[i].Action(_tree);
            }
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
