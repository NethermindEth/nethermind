// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Trie;
using Nethermind.State;
using Nethermind.Trie.Pruning;


public class Migrator
{
    public static void MigrateStateData(PatriciaTree stateTree, VerkleStateTree verkleStateTree, IStateReader stateReader, TrieStore trieStore)
    {
        var migrator = new AccountTreeMigrator(verkleStateTree, stateReader, trieStore);
        stateTree.Accept(migrator, stateTree.RootHash);
        migrator.FinalizeMigration();

        Console.WriteLine($"Verkle state tree root after migration: {verkleStateTree.StateRoot}");
    }
}
