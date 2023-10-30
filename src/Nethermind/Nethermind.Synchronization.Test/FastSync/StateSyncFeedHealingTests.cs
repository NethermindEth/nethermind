// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StateSyncFeedHealingTests : StateSyncFeedTestsBase
    {
        [Test]
        public async Task HealTreeWithoutBoundaryProofs()
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            TestItem.Tree.FillStateTreeWithTestAccounts(dbContext.RemoteStateTree);

            Hash256 rootHash = dbContext.RemoteStateTree.RootHash;

            ProcessAccountRange(dbContext.RemoteStateTree, dbContext.LocalStateTree, 1, rootHash, TestItem.Tree.AccountsWithPaths);

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);

            DetailedProgress data = ctx.TreeFeed.GetDetailedProgress();


            dbContext.CompareTrees("END");
            Assert.That(dbContext.LocalStateTree.RootHash, Is.EqualTo(dbContext.RemoteStateTree.RootHash));

            // I guess state root will be requested regardless
            Assert.That(data.RequestedNodesCount, Is.EqualTo(1));   // 4 boundary proof nodes stitched together => 0
        }

        [Test]
        public async Task HealBigSqueezedRandomTree()
        {
            DbContext dbContext = new DbContext(_logger, _logManager);

            int pathPoolCount = 100_000;
            Hash256[] pathPool = new Hash256[pathPoolCount];
            SortedDictionary<Hash256, Account> accounts = new();

            for (int i = 0; i < pathPoolCount; i++)
            {
                byte[] key = new byte[32];
                ((UInt256)i).ToBigEndian(key);
                Hash256 keccak = new Hash256(key);
                pathPool[i] = keccak;
            }

            // generate Remote Tree
            for (int accountIndex = 0; accountIndex < 10000; accountIndex++)
            {
                Account account = TestItem.GenerateRandomAccount();
                Hash256 path = pathPool[TestItem.Random.Next(pathPool.Length - 1)];

                dbContext.RemoteStateTree.Set(path, account);
                accounts[path] = account;
            }

            dbContext.RemoteStateTree.Commit(0);

            int startingHashIndex = 0;
            int endHashIndex;
            int blockJumps = 5;
            for (int blockNumber = 1; blockNumber <= blockJumps; blockNumber++)
            {
                for (int i = 0; i < 19; i++)
                {
                    endHashIndex = startingHashIndex + 1000;

                    ProcessAccountRange(dbContext.RemoteStateTree, dbContext.LocalStateTree, blockNumber, dbContext.RemoteStateTree.RootHash,
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
                            dbContext.RemoteStateTree.Set(path, account);
                            accounts[path] = account;
                        }
                        else
                        {
                            dbContext.RemoteStateTree.Set(path, null);
                            accounts.Remove(path);
                        }


                    }
                    else
                    {
                        dbContext.RemoteStateTree.Set(path, account);
                        accounts[path] = account;
                    }
                }

                dbContext.RemoteStateTree.Commit(blockNumber);
            }

            endHashIndex = startingHashIndex + 1000;
            while (endHashIndex < pathPool.Length - 1)
            {
                endHashIndex = startingHashIndex + 1000;
                if (endHashIndex > pathPool.Length - 1)
                {
                    endHashIndex = pathPool.Length - 1;
                }

                ProcessAccountRange(dbContext.RemoteStateTree, dbContext.LocalStateTree, blockJumps, dbContext.RemoteStateTree.RootHash,
                    accounts.Where(a => a.Key >= pathPool[startingHashIndex] && a.Key <= pathPool[endHashIndex]).Select(a => new PathWithAccount(a.Key, a.Value)).ToArray());


                startingHashIndex += 1000;
            }

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 9);

            DetailedProgress data = ctx.TreeFeed.GetDetailedProgress();

            dbContext.LocalStateTree.UpdateRootHash();
            dbContext.CompareTrees("END");
            _logger.Info($"REQUESTED NODES TO HEAL: {data.RequestedNodesCount}");
            Assert.IsTrue(data.RequestedNodesCount < accounts.Count / 2);
        }

        private static void ProcessAccountRange(StateTree remoteStateTree, StateTree localStateTree, int blockNumber, Hash256 rootHash, PathWithAccount[] accounts)
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

            _ = SnapProviderHelper.AddAccountRange(localStateTree, blockNumber, rootHash, startingHash, limitHash, accounts, firstProof.Concat(lastProof).ToArray());
        }
    }
}
