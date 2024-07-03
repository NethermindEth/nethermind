// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.State;
using Nethermind.Core.Extensions;

public class StorageTreeMigrator : ITreeVisitor
{
    private readonly VerkleStateTree _verkleStateTree;
    private readonly Address _address;
    private readonly Dictionary<StorageCell, byte[]> _storageItems = new Dictionary<StorageCell, byte[]>();

    public StorageTreeMigrator(VerkleStateTree verkleStateTree, Address address)
    {
        _verkleStateTree = verkleStateTree;
        _address = address;
    }

    public bool IsFullDbScan => true;

    public bool ShouldVisit(Hash256 nodeHash) => true;

    public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
        // No action needed
    }

    public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
        Console.WriteLine($"Warning: Missing node encountered in storage tree: {nodeHash}");
    }

    public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
    {
        // No action needed
    }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        // No action needed
    }

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        // TODO: verify if storage cell key is correct
        StorageCell storageKey = new StorageCell(_address, node.Key.ToUInt256());
        byte[] storageValue = value.ToArray();
        _storageItems[storageKey] = storageValue;
    }

    public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
        // No action needed for storage tree
    }

    public void FinalizeMigration()
    {
        if (_storageItems.Count > 0)
        {
            _verkleStateTree.BulkSet(_storageItems);
            Console.WriteLine($"Migrated {_storageItems.Count} storage items for address {_address}");
        }
    }
}

