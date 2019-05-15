/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Stats.Model;
using Nethermind.Store;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization
{
    [TestFixture]
    public class BlockDownloaderTests
    {
        private IBlockTree _blockTree;
        private ResponseBuilder _responseBuilder;
        private Dictionary<long, Keccak> _testHeaderMapping;

        private class ResponseBuilder
        {
            private IBlockTree _blockTree;
            private readonly Dictionary<long, Keccak> _testHeaderMapping;

            public ResponseBuilder(IBlockTree blockTree, Dictionary<long, Keccak> testHeaderMapping)
            {
                _blockTree = blockTree;
                _testHeaderMapping = testHeaderMapping;
            }

            public async Task<BlockHeader[]> BuildHeaderResponse(long startNumber, int number, Response flags)
            {
                bool consistent = flags.HasFlag(Response.Consistent);
                bool validSeals = flags.HasFlag(Response.ValidSeals);
                bool noEmptySpaces = flags.HasFlag(Response.NoEmptySpace);
                bool justFirst = flags.HasFlag(Response.JustFirstHeader);
                bool allKnown = flags.HasFlag(Response.AllKnown);
                bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);

                if (timeoutOnFullBatch && number == SyncBatchSize.Max)
                {
                    throw new TimeoutException();
                }

                BlockHeader startBlock = _blockTree.FindHeader(_testHeaderMapping[startNumber], false);
                BlockHeader[] headers = new BlockHeader[number];
                headers[0] = startBlock;
                if (!justFirst)
                {
                    for (int i = 1; i < number; i++)
                    {
                        headers[i] = consistent
                            ? Build.A.BlockHeader.WithParent(headers[i - 1]).TestObject
                            : Build.A.BlockHeader.WithNumber(headers[i - 1].Number + 1).TestObject;

                        if (allKnown)
                        {
                            _blockTree.SuggestHeader(headers[i]);
                        }

                        _testHeaderMapping[startNumber + i] = headers[i].Hash;
                    }
                }

                return await Task.FromResult(headers);
            }

            public async Task<Block[]> BuildBlocksResponse(Keccak[] blockHashes, Response flags)
            {
                bool consistent = flags.HasFlag(Response.Consistent);
                bool validSeals = flags.HasFlag(Response.ValidSeals);
                bool noEmptySpaces = flags.HasFlag(Response.NoEmptySpace);
                bool justFirst = flags.HasFlag(Response.JustFirstHeader);
                bool allKnown = flags.HasFlag(Response.AllKnown);
                bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);

                if (timeoutOnFullBatch && blockHashes.Length == SyncBatchSize.Max)
                {
                    throw new TimeoutException();
                }

                BlockHeader startBlock = _blockTree.FindHeader(blockHashes[0], false);
                if(startBlock == null) startBlock = Build.A.BlockHeader.WithHash(blockHashes[0]).TestObject;
                BlockHeader[] blockHeaders = new BlockHeader[blockHashes.Length];
                Block[] blocks = new Block[blockHashes.Length];
                blocks[0] = new Block(startBlock);
                blockHeaders[0] = startBlock;
                if (!justFirst)
                {
                    for (int i = 1; i < blockHashes.Length; i++)
                    {
                        blockHeaders[i] = consistent
                            ? Build.A.BlockHeader.WithParent(blockHeaders[i - 1]).TestObject
                            : Build.A.BlockHeader.WithNumber(blockHeaders[i - 1].Number + 1).TestObject;

                        _testHeaderMapping[startBlock.Number + i] = blockHeaders[i].Hash;

                        blocks[i] = consistent
                            ? Build.A.Block.WithHeader(blockHeaders[i]).TestObject
                            : Build.A.Block.WithHeader(blockHeaders[i - 1]).TestObject;

                        if (allKnown)
                        {
                            _blockTree.SuggestBlock(blocks[i]);
                        }
                    }
                }

                return await Task.FromResult(blocks);
            }
        }

        [SetUp]
        public void Setup()
        {
            Block genesis = Build.A.Block.Genesis.TestObject;
            _blockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            _blockTree.SuggestBlock(genesis);

            _testHeaderMapping = new Dictionary<long, Keccak>();
            _testHeaderMapping.Add(0, genesis.Hash);

            _responseBuilder = new ResponseBuilder(_blockTree, _testHeaderMapping);
        }

        [TestCase(0L)]
        [TestCase(32L)]
        [TestCase(33L)]
        [TestCase(SyncBatchSize.Max * 8)]
        public async Task Happy_path(long headNumber)
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = headNumber;

            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, headNumber - SyncModeSelector.FullSyncThreshold), _blockTree.BestSuggested.Number, "headers");

            peerInfo.HeadNumber *= 2;
            await blockDownloader.DownloadBlocks(peerInfo, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, headNumber * 2), _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Can_sync_with_peer_when_it_times_out_on_full_batch()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async ci => await _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.TimeoutOnFullBatch));
            
            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect | Response.TimeoutOnFullBatch));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = SyncBatchSize.Max * 2 + 32;

            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None).ContinueWith(t => { });
            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, peerInfo.HeadNumber - SyncModeSelector.FullSyncThreshold), _blockTree.BestSuggested.Number);
            
            peerInfo.HeadNumber *= 2;
            await blockDownloader.DownloadBlocks(peerInfo, CancellationToken.None).ContinueWith(t => { });
            await blockDownloader.DownloadBlocks(peerInfo, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, peerInfo.HeadNumber), _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Headers_already_known()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.AllKnown));
            
            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect | Response.AllKnown));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 64;

            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None)
                .ContinueWith(t => Assert.True(t.IsCompletedSuccessfully));
            
            peerInfo.HeadNumber = 128;
            await blockDownloader.DownloadBlocks(peerInfo, CancellationToken.None)
                .ContinueWith(t => Assert.True(t.IsCompletedSuccessfully));
        }
        
        [TestCase(33L)]
        [TestCase(65L)]
        public async Task Peer_sends_just_one_item_when_advertising_more_blocks(long headNumber)
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));
                    
            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect | Response.JustFirstHeader));
            
            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = headNumber;

            Task task = blockDownloader.DownloadBlocks(peerInfo, CancellationToken.None);
            await task.ContinueWith(t => Assert.True(t.IsFaulted));

            Assert.AreEqual(0, _blockTree.BestSuggested.Number);
        }

        [Test]
        public async Task Throws_on_null_best_peer()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);
            Task task1 = blockDownloader.DownloadHeaders(null, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            await task1.ContinueWith(t => Assert.True(t.IsFaulted));

            Task task2 = blockDownloader.DownloadBlocks(null, CancellationToken.None);
            await task2.ContinueWith(t => Assert.True(t.IsFaulted));
        }

        [Test]
        public async Task Throws_on_inconsistent_batch()
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect ^ Response.Consistent));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 1024;

            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);
            Task task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            await task.ContinueWith(t => Assert.True(t.IsFaulted));
        }

        [Test]
        public async Task Throws_on_invalid_seal()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.NeverValid, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 1000;

            Task task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            await task.ContinueWith(t => Assert.True(t.IsFaulted));
        }

        [Test]
        public async Task Throws_on_invalid_header()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.NeverValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 1000;

            Task task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            await task.ContinueWith(t => Assert.True(t.IsFaulted));
        }

        private class SlowSealValidator : ISealValidator
        {
            public bool ValidateParams(BlockHeader parent, BlockHeader header)
            {
                Thread.Sleep(1000);
                return true;
            }

            public bool ValidateSeal(BlockHeader header)
            {
                Thread.Sleep(1000);
                return true;
            }
        }

        private class SlowHeaderValidator : IBlockValidator
        {
            public bool ValidateHeader(BlockHeader header, BlockHeader parent, bool isOmmer)
            {
                Thread.Sleep(1000);
                return true;
            }

            public bool ValidateHeader(BlockHeader header, bool isOmmer)
            {
                Thread.Sleep(1000);
                return true;
            }

            public bool ValidateSuggestedBlock(Block block)
            {
                Thread.Sleep(1000);
                return true;
            }

            public bool ValidateProcessedBlock(Block processedBlock, TransactionReceipt[] receipts, Block suggestedBlock)
            {
                Thread.Sleep(1000);
                return true;
            }
        }

        [Test, MaxTime(7000)]
        public async Task Can_cancel_seal_validation()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, new SlowSealValidator(), LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect));
            
            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 1000;

            CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1000);
            Task task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, cancellation.Token);
            await task.ContinueWith(t => Assert.True(t.IsCanceled, $"headers {t.Status}"));
            
            peerInfo.HeadNumber = 2000;
            cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1000);
            task = blockDownloader.DownloadBlocks(peerInfo, cancellation.Token);
            await task.ContinueWith(t => Assert.True(t.IsCanceled, $"blocks {t.Status}"));
        }

        [Test, MaxTime(7000)]
        public async Task Can_cancel_adding_headers()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, new SlowHeaderValidator(), TestSealValidator.AlwaysValid, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));
            
            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 1000;

            CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1000);
            Task task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, cancellation.Token);
            await task.ContinueWith(t => Assert.True(t.IsCanceled, "headers"));
            
            peerInfo.HeadNumber *= 2;
            cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1000);
            task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, cancellation.Token);
            await task.ContinueWith(t => Assert.True(t.IsCanceled, "blocks"));
        }

        private class ThrowingPeer : ISyncPeer
        {
            public Guid SessionId { get; }
            public bool IsFastSyncSupported { get; }
            public Node Node { get; }
            public string ClientId => "EX peer";
            public UInt256 TotalDifficultyOnSessionStart => UInt256.MaxValue;

            public void Disconnect(DisconnectReason reason, string details)
            {
                throw new NotImplementedException();
            }

            public Task<Block[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
            {
                throw new Exception();
            }

            public Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public void SendNewBlock(Block block)
            {
                throw new NotImplementedException();
            }

            public void SendNewTransaction(Transaction transaction)
            {
                throw new NotImplementedException();
            }

            public Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(Keccak[] hashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }
        }

        [Test]
        public async Task Faults_on_get_headers_faulting()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, LimboLogs.Instance);

            ISyncPeer syncPeer = new ThrowingPeer();
            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 1000;

            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None)
                .ContinueWith(t => Assert.True(t.IsFaulted));
        }

        private Task<BlockHeader[]> Throw(CallInfo arg)
        {
            throw new Exception();
        }

        [Flags]
        private enum Response
        {
            Consistent = 1,
            ValidSeals = 2,
            NoEmptySpace = 4,
            AllCorrect = 7,
            JustFirstHeader = 8,
            AllKnown = 16,
            TimeoutOnFullBatch = 32,
        }
    }
}