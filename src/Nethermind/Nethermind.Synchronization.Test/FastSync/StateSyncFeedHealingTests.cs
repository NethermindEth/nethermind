// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StateSyncFeedHealingTests : StateSyncFeedTestsBase
    {
        public StateSyncFeedHealingTests(TrieNodeResolverCapability capability) : base(capability) { }

        [Test]
        public async Task HealTreeWithoutBoundaryProofs()
        {
            DbContext dbContext = new DbContext(_resolverCapability, _logger, _logManager);
            TestItem.Tree.FillStateTreeWithTestAccounts(dbContext.RemoteStateTree);

            Keccak rootHash = dbContext.RemoteStateTree.RootHash;

            ProcessAccountRange(dbContext.RemoteStateTree, dbContext.LocalStateTree, 1, rootHash, TestItem.Tree.AccountsWithPaths);

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 1024);

            DetailedProgress data = ctx.TreeFeed.GetDetailedProgress();


            dbContext.CompareTrees("END");
            Assert.AreEqual(dbContext.RemoteStateTree.RootHash, dbContext.LocalStateTree.RootHash);

            // I guess state root will be requested regardless
            // Assert.AreEqual(1, data.RequestedNodesCount);   // 4 boundary proof nodes stitched together => 0
        }

        void LogRemoteStateTreeStats(DbContext context, Keccak? rootHash)
        {
            TrieStatsCollector collector = new(context.RemoteCodeDb, _logManager);
            context.RemoteStateTree.Accept(collector, rootHash?? context.RemoteStateTree.RootHash, new VisitingOptions { MaxDegreeOfParallelism = Environment.ProcessorCount });
            _logger.Info($"REMOTE STATE: Starting from {rootHash?? context.RemoteStateTree.RootHash} {Environment.NewLine}" + collector.Stats);
        }

        void LogLocalStateTreeStats(DbContext context, Keccak? rootHash)
        {
            TrieStatsCollector collector = new(context.LocalCodeDb, _logManager);
            context.RemoteStateTree.Accept(collector, rootHash?? context.LocalStateTree.RootHash, new VisitingOptions { MaxDegreeOfParallelism = Environment.ProcessorCount });
            _logger.Info($"LOCAL STATE: Starting from {rootHash?? context.LocalStateTree.RootHash} {Environment.NewLine}" + collector.Stats);
        }


        [Test]
        public async Task HealBigSqueezedRandomTree()
        {
            DbContext dbContext = new DbContext(_resolverCapability, _logger, _logManager);

            int pathPoolCount = 100_000;
            Keccak[] pathPool = new Keccak[pathPoolCount];
            SortedDictionary<Keccak, Account> accounts = new();
            int updatesCount = 0;
            int deletionsCount = 0;

            for (int i = 0; i < pathPoolCount; i++)
            {
                byte[] key = new byte[32];
                ((UInt256)i).ToBigEndian(key);
                Keccak keccak = new Keccak(key);
                pathPool[i] = keccak;
            }
            _logger.Info($"Path Pool Created: {pathPool.Length}");

            // generate Remote Tree
            for (int accountIndex = 0; accountIndex < 10000; accountIndex++)
            {
                Account account = TestItem.GenerateRandomAccount();
                Keccak path = pathPool[TestItem.Random.Next(pathPool.Length - 1)];

                dbContext.RemoteStateTree.Set(path, account);
                accounts[path] = account;
            }

            _logger.Info($"Accounts Created: {accounts.Count}");

            dbContext.RemoteStateTree.Commit(0);

            int startingHashIndex = 0;
            int endHashIndex = 0;
            int blockJumps = 5;
            for (int blockNumber = 1; blockNumber <= blockJumps; blockNumber++)
            {
                _logger.Info($"Starting for block number {blockNumber}");
                _logger.Info($"startingHashIndex {startingHashIndex} endHashIndex: {endHashIndex}");
                LogRemoteStateTreeStats(dbContext, null);
                LogLocalStateTreeStats(dbContext, null);
                for (int i = 0; i < 19; i++)
                {
                    _logger.Info($"inner for loop: i: {i} startingHashIndex: {startingHashIndex} endHashIndex: {startingHashIndex + 1000}");
                    endHashIndex = startingHashIndex + 1000;

                    ProcessAccountRange(dbContext.RemoteStateTree, dbContext.LocalStateTree, blockNumber, dbContext.RemoteStateTree.RootHash,
                       accounts.Where(a => a.Key >= pathPool[startingHashIndex] && a.Key <= pathPool[endHashIndex]).Select(a => new PathWithAccount(a.Key, a.Value)).ToArray());

                    startingHashIndex = endHashIndex + 1;
                }
                LogRemoteStateTreeStats(dbContext, null);
                LogLocalStateTreeStats(dbContext, null);

                for (int accountIndex = 0; accountIndex < 1000; accountIndex++)
                {
                    Account account = TestItem.GenerateRandomAccount();
                    Keccak path = pathPool[TestItem.Random.Next(pathPool.Length - 1)];

                    if (accounts.ContainsKey(path))
                    {
                        if (TestItem.Random.NextSingle() > 0.5)
                        {
                            dbContext.RemoteStateTree.Set(path, account);
                            accounts[path] = account;
                            updatesCount++;
                        }
                        else
                        {
                            dbContext.RemoteStateTree.Set(path, null);
                            accounts.Remove(path);
                            deletionsCount++;
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
            _logger.Info("Complete Block Insertion");
            LogRemoteStateTreeStats(dbContext, null);
            LogLocalStateTreeStats(dbContext, null);

            endHashIndex = startingHashIndex + 1000;
            while (endHashIndex < pathPool.Length - 1)
            {

                endHashIndex = startingHashIndex + 1000;
                _logger.Info($"startingHashIndex {startingHashIndex} endHashIndex: {endHashIndex}");
                if (endHashIndex > pathPool.Length - 1)
                {
                    endHashIndex = pathPool.Length - 1;
                }

                ProcessAccountRange(dbContext.RemoteStateTree, dbContext.LocalStateTree, blockJumps, dbContext.RemoteStateTree.RootHash,
                    accounts.Where(a => a.Key >= pathPool[startingHashIndex] && a.Key <= pathPool[endHashIndex]).Select(a => new PathWithAccount(a.Key, a.Value)).ToArray());


                startingHashIndex += 1000;
            }

            LogRemoteStateTreeStats(dbContext, null);
            LogLocalStateTreeStats(dbContext, null);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;

            SafeContext ctx = PrepareDownloader(dbContext);
            await ActivateAndWait(ctx, dbContext, 9);

            DetailedProgress data = ctx.TreeFeed.GetDetailedProgress();

            dbContext.LocalStateTree.UpdateRootHash();
            LogRemoteStateTreeStats(dbContext, null);
            LogLocalStateTreeStats(dbContext, null);
            // dbContext.CompareTrees("END");
            _logger.Info($"REQUESTED NODES TO HEAL: {data.RequestedNodesCount}");
            Assert.IsTrue(data.RequestedNodesCount < accounts.Count / 2);
        }

        private static void ProcessAccountRange(IStateTree remoteStateTree, IStateTree localStateTree, int blockNumber, Keccak rootHash, PathWithAccount[] accounts)
        {
            Keccak startingHash = accounts.First().Path;
            Keccak endHash = accounts.Last().Path;
            Keccak limitHash = Keccak.MaxValue;

            AccountProofCollector accountProofCollector = new(startingHash.Bytes);
            remoteStateTree.Accept(accountProofCollector, remoteStateTree.RootHash);
            byte[][] firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(endHash.Bytes);
            remoteStateTree.Accept(accountProofCollector, remoteStateTree.RootHash);
            byte[][] lastProof = accountProofCollector.BuildResult().Proof;

            (_, _, _, _) = SnapProviderHelper.AddAccountRange(localStateTree, blockNumber, rootHash, startingHash, limitHash, accounts, firstProof!.Concat(lastProof!).ToArray());
        }
    }
}
