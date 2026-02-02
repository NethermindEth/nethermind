// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

[TestFixtureSource(typeof(TreeSyncStoreTestFixtureSource))]
[Parallelizable(ParallelScope.All)]
public class StateSyncFeedHealingTests(Action<ContainerBuilder> registerTreeSyncStore)
    : StateSyncFeedTestsBase(registerTreeSyncStore)
{
    [Test]
    public async Task HealTreeWithoutBoundaryProofs()
    {
        LocalDbContext local = new(_logManager);
        RemoteDbContext remote = new(_logManager);
        TestItem.Tree.FillStateTreeWithTestAccounts(remote.StateTree);

        Hash256 rootHash = remote.StateTree.RootHash;

        ProcessAccountRange(remote.StateTree, local.SnapTrieFactory, 1, rootHash, TestItem.Tree.AccountsWithPaths);

        await using IContainer container = PrepareDownloader(local, remote);
        SafeContext ctx = container.Resolve<SafeContext>();
        await ActivateAndWait(ctx);

        DetailedProgress data = ctx.TreeFeed.GetDetailedProgress();

        local.CompareTrees(remote, _logger, "END");
        Assert.That(local.RootHash, Is.EqualTo(remote.StateTree.RootHash));

        // I guess state root will be requested regardless
        Assert.That(data.RequestedNodesCount, Is.EqualTo(1));   // 4 boundary proof nodes stitched together => 0
    }

    [Test]
    public async Task HealBigSqueezedRandomTree()
    {
        LocalDbContext local = new(_logManager);
        RemoteDbContext remote = new(_logManager);

        int pathPoolCount = 100_000;
        Hash256[] pathPool = new Hash256[pathPoolCount];
        SortedDictionary<Hash256, Account> accounts = new();

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            // Snap can't actually use GetTrieNodes where the path is exactly 64 nibble. So *255.
            ((UInt256)(i * 255)).ToBigEndian(key);
            Hash256 keccak = new Hash256(key);
            pathPool[i] = keccak;
        }

        // generate Remote Tree
        for (int accountIndex = 0; accountIndex < 10000; accountIndex++)
        {
            Account account = TestItem.GenerateRandomAccount();
            Hash256 path = pathPool[TestItem.Random.Next(pathPool.Length - 1)];

            remote.StateTree.Set(path, account);
            accounts[path] = account;
        }

        remote.StateTree.Commit();

        int startingHashIndex = 0;
        int endHashIndex;
        int blockJumps = 5;
        for (int blockNumber = 1; blockNumber <= blockJumps; blockNumber++)
        {
            for (int i = 0; i < 19; i++)
            {
                endHashIndex = startingHashIndex + 1000;

                ProcessAccountRange(remote.StateTree, local.SnapTrieFactory, blockNumber, remote.StateTree.RootHash,
                   accounts.Where(a => a.Key >= pathPool[startingHashIndex] && a.Key <= pathPool[endHashIndex]).Select(a => new PathWithAccount(a.Key, a.Value)).ToArray());

                startingHashIndex = endHashIndex + 1;
            }

            for (int accountIndex = 0; accountIndex < 1000; accountIndex++)
            {
                Account account = TestItem.GenerateRandomAccount();
                Hash256 path = pathPool[TestItem.Random.Next(pathPool.Length - 1)];

                if (accounts.ContainsKey(path))
                {
                    if (TestItem.Random.NextSingle() > 0.5)
                    {
                        remote.StateTree.Set(path, account);
                        accounts[path] = account;
                    }
                    else
                    {
                        remote.StateTree.Set(path, null);
                        accounts.Remove(path);
                    }


                }
                else
                {
                    remote.StateTree.Set(path, account);
                    accounts[path] = account;
                }
            }

            remote.StateTree.Commit();
        }

        endHashIndex = startingHashIndex + 1000;
        while (endHashIndex < pathPool.Length - 1)
        {
            endHashIndex = startingHashIndex + 1000;
            if (endHashIndex > pathPool.Length - 1)
            {
                endHashIndex = pathPool.Length - 1;
            }

            ProcessAccountRange(remote.StateTree, local.SnapTrieFactory, blockJumps, remote.StateTree.RootHash,
                accounts.Where(a => a.Key >= pathPool[startingHashIndex] && a.Key <= pathPool[endHashIndex]).Select(a => new PathWithAccount(a.Key, a.Value)).ToArray());


            startingHashIndex += 1000;
        }

        local.RootHash = remote.StateTree.RootHash;

        await using IContainer container = PrepareDownloader(local, remote, syncDispatcherAllocateTimeoutMs: 1000);
        SafeContext ctx = container.Resolve<SafeContext>();
        await ActivateAndWait(ctx, timeout: 20000);

        DetailedProgress data = ctx.TreeFeed.GetDetailedProgress();

        local.UpdateRootHash();
        local.CompareTrees(remote, _logger, "END");
        _logger.Info($"REQUESTED NODES TO HEAL: {data.RequestedNodesCount}");
        Assert.That(data.RequestedNodesCount, Is.LessThan(accounts.Count / 2));
    }

    private static void ProcessAccountRange(StateTree remoteStateTree, ISnapTrieFactory snapTrieFactory, int blockNumber, Hash256 rootHash, PathWithAccount[] accounts)
    {
        ValueHash256 startingHash = accounts.First().Path;
        ValueHash256 endHash = accounts.Last().Path;
        Hash256 limitHash = Keccak.MaxValue;

        AccountProofCollector accountProofCollector = new(startingHash.Bytes);
        remoteStateTree.Accept(accountProofCollector, remoteStateTree.RootHash);
        byte[][] firstProof = accountProofCollector.BuildResult().Proof!;
        accountProofCollector = new(endHash.Bytes);
        remoteStateTree.Accept(accountProofCollector, remoteStateTree.RootHash);
        byte[][] lastProof = accountProofCollector.BuildResult().Proof!;

        _ = SnapProviderHelper.AddAccountRange(snapTrieFactory.CreateStateTree(), blockNumber, rootHash, startingHash, limitHash, accounts, firstProof.Concat(lastProof).ToArray());
    }
}
