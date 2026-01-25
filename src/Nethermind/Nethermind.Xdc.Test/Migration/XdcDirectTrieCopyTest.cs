// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using LevelDB;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Xdc.Migration;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Migration;

// Direct trie node copy migration test, copies all trie nodes
// under the latest state root directly from XDC LevelDB to the Nethermind database
public class XdcDirectTrieCopyTest : TestWithLevelDbFix
{
    private static readonly byte[] HeadHeaderKey = "LastHeader"u8.ToArray();
    private static readonly byte[] HeaderPrefix = "h"u8.ToArray();
    private static readonly byte[] HeaderNumberPrefix = "H"u8.ToArray();

    // Some known test addresses for verification
    // TODO: move balance/code test here
    private static readonly Address[] TestAddresses =
    [
        new("0x863211afe152783454003874d0e127c1ca7ad92b"),
        new("0x0000000000000000000000000000000000000000"),
        new("0x0000000000000000000000000000000000000001"),
        new("0x0000000000000000000000000000000000000002"),
        new("0x0000000000000000000000000000000000000003"),
    ];

    [TestCase(@"D:\Nethermind\xdc\chaindata")]
    public void DirectTrieCopy(string xdcDbPath)
    {
        using SourceContext sourceContext = OpenSourceDatabase(xdcDbPath);
        using TargetContext targetContext = OpenTargetDatabase();

        MigrationStats stats = MigrateTrie(sourceContext, targetContext);
        TestContext.Out.WriteLine($"{stats}");

        StateTree sourceTree = sourceContext.StateTree;
        StateTree targetTree = new(targetContext.TrieStore, NullLogManager.Instance) { RootHash = sourceContext.StateRoot };

        VerifyNoMissingNodes(sourceTree, targetTree);
        VerifyAccounts(sourceTree, targetTree);
        VerifyStorage(sourceTree, targetTree);
    }

    private static SourceContext OpenSourceDatabase(string dbPath)
    {
        var options = new Options { CreateIfMissing = false };
        var levelDb = new DB(options, dbPath);

        byte[]? lastHeaderHash = levelDb.Get(HeadHeaderKey);
        Assert.That(lastHeaderHash, Is.Not.Null.And.Not.Empty, "LastHeader not found");

        byte[] headerNumberKey = [.. HeaderNumberPrefix, .. lastHeaderHash];
        byte[]? blockNumberBytes = levelDb.Get(headerNumberKey);
        Assert.That(blockNumberBytes, Is.Not.Null.And.Length.EqualTo(8), "Block number not found");
        ulong blockNumber = BinaryPrimitives.ReadUInt64BigEndian(blockNumberBytes);

        byte[] headerKey = [.. HeaderPrefix, .. blockNumber.ToBigEndianByteArray(), .. lastHeaderHash];
        byte[]? headerRlp = levelDb.Get(headerKey);
        Assert.That(headerRlp, Is.Not.Null.And.Not.Empty, "Header RLP not found");

        BlockHeader header = new XdcHeaderDecoder().Decode(headerRlp)!;
        Hash256 stateRoot = header.StateRoot!;

        return new SourceContext(levelDb, stateRoot, blockNumber);
    }

    private static TargetContext OpenTargetDatabase()
    {
        var stateDb = new MemDb(DbNames.State);
        var codeDb = new MemDb(DbNames.Code);

        // Use Hash scheme - same as XDC
        var nodeStorage = new NodeStorage(stateDb, INodeStorage.KeyScheme.Hash, requirePath: false);

        return new TargetContext(stateDb, codeDb, nodeStorage);
    }

    private static MigrationStats MigrateTrie(SourceContext source, TargetContext target)
    {
        var result = new MigrationStats();

        // TODO: copy directly during enumeration?
        // TODO: use WriteBatch
        var nodeCollector = new TrieNodeHashCollector(source.StateRoot);
        source.StateTree.Accept(nodeCollector, source.StateRoot, new() { FullScanMemoryBudget = 0 }); // TODO: enable batching?
        foreach (Hash256 hash in nodeCollector.Hashes)
        {
            if (source.DbAdapter.Get(hash.Bytes) is not {} value)
                continue;

            target.StateDb.Set(hash.Bytes, value);
            result.TrieNodesCopied++;
        }

        // TODO: union with previous step or verify no intersections
        var codeCollector = new CodeHashCollector();
        source.StateTree.Accept(codeCollector, source.StateRoot, new() { FullScanMemoryBudget = 0 });
        foreach (Hash256 codeHash in codeCollector.CodeHashes)
        {
            if (source.DbAdapter.Get(codeHash.Bytes) is not {} code)
                continue;

            target.CodeDb.Set(codeHash.Bytes, code);
            result.CodeEntriesCopied++;
        }

        return result;
    }

    private static void VerifyNoMissingNodes(StateTree sourceTree, StateTree targetTree)
    {
        var counter = new TrieNodeCounter();

        targetTree.Accept(counter, sourceTree.RootHash, new() { FullScanMemoryBudget = 0 });
        TestContext.Out.WriteLine($"{counter}");

        Assert.That(counter.MissingCount, Is.Zero);
    }

    private static void VerifyAccounts(StateTree sourceTree, StateTree targetTree)
    {
        using IDisposable assertScope = Assert.EnterMultipleScope();

        foreach (Address address in TestAddresses)
        {
            Account? sourceAccount = sourceTree.Get(address);
            Account? targetAccount = targetTree.Get(address);

            Assert.That(sourceAccount, Is.EqualTo(targetAccount), $"Account mismatch for {address}");
        }
    }

    private static void VerifyStorage(StateTree sourceTree, StateTree targetTree)
    {
        using IDisposable assertScope = Assert.EnterMultipleScope();

        foreach (Address address in TestAddresses)
        {
            Account? sourceAccount = sourceTree.Get(address);
            if (sourceAccount is null || !sourceAccount.HasStorage || sourceAccount.StorageRoot == Keccak.EmptyTreeHash)
                continue;

            var sourceAccountTree = new StorageTree(sourceTree.TrieStore, sourceAccount.StorageRoot, NullLogManager.Instance);
            var targetStorageTree = new StorageTree(targetTree.TrieStore, sourceAccount.StorageRoot, NullLogManager.Instance);

            var slotCollector = new StorageSlotCollector();
            sourceAccountTree.Accept(slotCollector, sourceAccount.StorageRoot, new() { FullScanMemoryBudget = 0 });

            foreach ((ValueHash256 keyHash, var sourceValue) in slotCollector.Slots)
                Assert.That(targetStorageTree.Get(keyHash), Is.EqualTo(sourceValue), $"Storage mismatch for {address}");
        }
    }

    // Collects all trie node hashes
    private sealed class TrieNodeHashCollector(Hash256 stateRoot) : ITreeVisitor<TreePathContextWithStorage>
    {
        public bool IsFullDbScan => true;
        public HashSet<Hash256> Hashes { get; } = [stateRoot];

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash)
        {
            Hashes.Add(rootHash.ToCommitment());
        }

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode)
        {
            Hashes.Add(nextNode.ToCommitment());
            return true;
        }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash)
        {
            // TODO: throw an error?
        }

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            if (node.Keccak is not null)
                Hashes.Add(node.Keccak);
        }

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            if (node.Keccak is not null)
                Hashes.Add(node.Keccak);
        }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            if (node.Keccak is not null)
                Hashes.Add(node.Keccak);
        }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account)
        {
            // Add storage trie root if present, TODO: verify if needed
            if (account.HasStorage && account.StorageRoot != Keccak.EmptyTreeHash)
                Hashes.Add(account.StorageRoot.ToCommitment());
        }
    }

    // Collects code hashes from accounts
    private sealed class CodeHashCollector : ITreeVisitor<TreePathContextWithStorage>
    {
        public bool IsFullDbScan => true;

        public HashSet<Hash256> CodeHashes { get; } = [];

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) { }

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account)
        {
            if (account.HasCode)
                CodeHashes.Add(account.CodeHash.ToCommitment());
        }
    }

    // Counts nodes and missing nodes
    private sealed class TrieNodeCounter : ITreeVisitor<TreePathContextWithStorage>
    {
        public int BranchCount { get; private set; }
        public int ExtensionCount { get; private set; }
        public int LeafCount { get; private set; }
        public int AccountCount { get; private set; }
        public int MissingCount { get; private set; }

        public bool IsFullDbScan => true;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) => MissingCount++;
        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => BranchCount++;
        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => ExtensionCount++;
        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) => LeafCount++;
        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) => AccountCount++;

        public override string ToString() =>
            $"Branch nodes: {BranchCount}, extension: {ExtensionCount}, leaf nodes: {LeafCount}, account: {AccountCount}, missing: {MissingCount}";
    }

    // Collects storage slots
    private sealed class StorageSlotCollector : ITreeVisitor<TreePathContextWithStorage>
    {
        public List<(ValueHash256 keyHash, byte[] value)> Slots { get; } = [];

        public bool IsFullDbScan => true;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) { }
        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) { }
        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) { }
        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            // TODO: verify lenght check
            byte[]? value = node.Value.ToArray();
            if (value is not null && value.Length > 0 && nodeContext.Path.Length == 64)
                Slots.Add((nodeContext.Path.Path, value));
        }
    }

    private sealed class SourceContext : IDisposable
    {
        private readonly DB _levelDb;

        public Hash256 StateRoot { get; }
        public ulong BlockNumber { get; }
        public LevelReadOnlyDbAdapter DbAdapter { get; }
        public IScopedTrieStore TrieStore { get; }
        public StateTree StateTree { get; }

        public SourceContext(DB levelDb, Hash256 stateRoot, ulong blockNumber)
        {
            _levelDb = levelDb;

            StateRoot = stateRoot;
            BlockNumber = blockNumber;
            DbAdapter = new(levelDb);
            TrieStore = new ReadOnlyScopedTrieStore(DbAdapter);
            StateTree = new(TrieStore, NullLogManager.Instance) { RootHash = stateRoot };
        }

        public void Dispose()
        {
            DbAdapter.Dispose();
            _levelDb.Dispose();
        }
    }

    private sealed class TargetContext : IDisposable
    {
        public IDb StateDb { get; }
        public IDb CodeDb { get; }
        public NodeStorage NodeStorage { get; }
        public IScopedTrieStore TrieStore { get; }

        public TargetContext(MemDb stateDb, MemDb codeDb, NodeStorage nodeStorage)
        {
            StateDb = stateDb;
            CodeDb = codeDb;
            NodeStorage = nodeStorage;
            TrieStore = new RawScopedTrieStore(nodeStorage);
        }

        public void Dispose()
        {
            StateDb.Dispose();
            CodeDb.Dispose();
        }
    }

    private sealed class MigrationStats
    {
        public int TrieNodesCopied { get; set; }
        public int CodeEntriesCopied { get; set; }

        public override string ToString() => $"Trie nodes copied: {TrieNodesCopied}, Code entries copied: {CodeEntriesCopied}";
    }
}
