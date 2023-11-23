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
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Commit(1);
            }),
            ("set_3_delete_1", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit(1);
            }),
            ("set_3_delete_2", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Commit(1);
            }),
            ("set_3_delete_all", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
                tree.Commit(1);
            }),
            ("extension_read_full_match", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_read_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_new_branch", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extension_delete_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("extenson_create_new_extension", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_new_value", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_no_change", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_delete", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_delete_missing", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_update_extension", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_read", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("leaf_update_missing", tree =>
            {
                tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
                Account account = tree.Get(new Hash256("111111111111111111111111111111111111111111111111111111111ddddddd"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_update_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_read_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
                tree.Commit(1);
            }),
            ("branch_delete_missing", tree =>
            {
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
                tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
                tree.UpdateRootHash();
                Hash256 rootHash = tree.RootHash;
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
