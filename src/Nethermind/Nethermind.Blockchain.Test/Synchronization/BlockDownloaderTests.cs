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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Stats.Model;
using Nethermind.Store;
using Nethermind.Store.Repositories;
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
                bool justFirst = flags.HasFlag(Response.JustFirst);
                bool allKnown = flags.HasFlag(Response.AllKnown);
                bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
                bool noBody = flags.HasFlag(Response.NoBody);

                if (timeoutOnFullBatch && number == SyncBatchSize.Max)
                {
                    throw new TimeoutException();
                }

                BlockHeader startBlock = _blockTree.FindHeader(_testHeaderMapping[startNumber], BlockTreeLookupOptions.None);
                BlockHeader[] headers = new BlockHeader[number];
                headers[0] = startBlock;
                if (!justFirst)
                {
                    for (int i = 1; i < number; i++)
                    {
                        
                        headers[i] = consistent
                            ? Build.A.BlockHeader.WithParent(headers[i - 1]).WithOmmersHash(noBody ? Keccak.OfAnEmptySequenceRlp : Keccak.Zero).TestObject
                            : Build.A.BlockHeader.WithNumber(headers[i - 1].Number + 1).TestObject;

                        if (allKnown)
                        {
                            _blockTree.SuggestHeader(headers[i]);
                        }

                        _testHeaderMapping[startNumber + i] = headers[i].Hash;
                    }
                }

                BlockHeadersMessage message = new BlockHeadersMessage(headers);
                byte[] messageSerialized = _headersSerializer.Serialize(message);
                return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
            }

            private BlockHeadersMessageSerializer _headersSerializer = new BlockHeadersMessageSerializer();
            private BlockBodiesMessageSerializer _bodiesSerializer = new BlockBodiesMessageSerializer();

            public async Task<BlockBody[]> BuildBlocksResponse(Keccak[] blockHashes, Response flags)
            {
                bool consistent = flags.HasFlag(Response.Consistent);
                bool validSeals = flags.HasFlag(Response.ValidSeals);
                bool noEmptySpaces = flags.HasFlag(Response.NoEmptySpace);
                bool justFirst = flags.HasFlag(Response.JustFirst);
                bool allKnown = flags.HasFlag(Response.AllKnown);
                bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);

                if (timeoutOnFullBatch && blockHashes.Length == SyncBatchSize.Max)
                {
                    throw new TimeoutException();
                }

                BlockHeader startHeader = _blockTree.FindHeader(blockHashes[0], BlockTreeLookupOptions.None);
                if (startHeader == null) startHeader = Build.A.BlockHeader.WithHash(blockHashes[0]).TestObject;

                BlockHeader[] blockHeaders = new BlockHeader[blockHashes.Length];
                BlockBody[] blockBodies = new BlockBody[blockHashes.Length];
                blockBodies[0] = new BlockBody(new Transaction[0], new BlockHeader[0]);
                blockHeaders[0] = startHeader;
                if (!justFirst)
                {
                    for (int i = 1; i < blockHashes.Length; i++)
                    {
                        blockHeaders[i] = consistent
                            ? Build.A.BlockHeader.WithParent(blockHeaders[i - 1]).TestObject
                            : Build.A.BlockHeader.WithNumber(blockHeaders[i - 1].Number + 1).TestObject;

                        _testHeaderMapping[startHeader.Number + i] = blockHeaders[i].Hash;

                        Block block = consistent
                            ? Build.A.Block.WithHeader(blockHeaders[i]).TestObject
                            : Build.A.Block.WithHeader(blockHeaders[i - 1]).TestObject;
                        blockBodies[i] = new BlockBody(block.Transactions, block.Ommers);

                        if (allKnown)
                        {
                            _blockTree.SuggestBlock(block);
                        }
                    }
                }

                BlockBodiesMessage message = new BlockBodiesMessage(blockBodies);
                byte[] messageSerialized = _bodiesSerializer.Serialize(message);
                return await Task.FromResult(_bodiesSerializer.Deserialize(messageSerialized).Bodies);
            }
        }

        [SetUp]
        public void Setup()
        {
            Block genesis = Build.A.Block.Genesis.TestObject;
            var blockInfoDb = new MemDb();
            _blockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = headNumber;

            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, headNumber - SyncModeSelector.FullSyncThreshold), _blockTree.BestSuggestedHeader.Number, "headers");

            peerInfo.HeadNumber *= 2;
            await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, headNumber * 2), _blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public async Task Can_sync_with_peer_when_it_times_out_on_full_batch()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

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
            Assert.AreEqual(Math.Max(0, peerInfo.HeadNumber - SyncModeSelector.FullSyncThreshold), _blockTree.BestSuggestedHeader.Number);

            peerInfo.HeadNumber *= 2;
            await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None).ContinueWith(t => { });
            await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, peerInfo.HeadNumber), _blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public async Task Headers_already_known()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

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
            await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None)
                .ContinueWith(t => Assert.True(t.IsCompletedSuccessfully));
        }

        [TestCase(33L)]
        [TestCase(65L)]
        public async Task Peer_sends_just_one_item_when_advertising_more_blocks(long headNumber)
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect | Response.JustFirst));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = headNumber;

            Task task = blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None);
            await task.ContinueWith(t => Assert.True(t.IsFaulted));

            Assert.AreEqual(0, _blockTree.BestSuggestedHeader.Number);
        }
        
        [TestCase(33L)]
        [TestCase(65L)]
        public async Task Peer_sends_just_one_item_when_advertising_more_blocks_but_no_bodies(long headNumber)
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.NoBody));

            syncPeer.GetBlocks(Arg.Any<Keccak[]>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<Keccak[]>(0), Response.AllCorrect | Response.JustFirst));

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = headNumber;

            Task task = blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None);
            await task.ContinueWith(t => Assert.False(t.IsFaulted));

            Assert.AreEqual(headNumber, _blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public async Task Throws_on_null_best_peer()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);
            Task task1 = blockDownloader.DownloadHeaders(null, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            await task1.ContinueWith(t => Assert.True(t.IsFaulted));

            Task task2 = blockDownloader.DownloadBlocks(null, 0, CancellationToken.None);
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

            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);
            Task task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            await task.ContinueWith(t => Assert.True(t.IsFaulted));
        }

        [Test]
        public async Task Throws_on_invalid_seal()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.NeverValid, NullSyncReport.Instance, LimboLogs.Instance);

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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.NeverValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

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
            public bool ValidateHash(BlockHeader header)
            {
                Thread.Sleep(1000);
                return true;
            }

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

            public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
            {
                Thread.Sleep(1000);
                return true;
            }
        }

        [Test, MaxTime(7000)]
        [Ignore("Fails OneLoggerLogManager Travis only")]
        public async Task Can_cancel_seal_validation()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, new SlowSealValidator(), NullSyncReport.Instance, LimboLogs.Instance);

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
            task = blockDownloader.DownloadBlocks(peerInfo, 0, cancellation.Token);
            await task.ContinueWith(t => Assert.True(t.IsCanceled, $"blocks {t.Status}"));
        }

        [Test, MaxTime(7000)]
        public async Task Can_cancel_adding_headers()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, new SlowHeaderValidator(), TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

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

            public Task<BlockBody[]> GetBlocks(Keccak[] blockHashes, CancellationToken token)
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

            public Task<TxReceipt[][]> GetReceipts(Keccak[] blockHash, CancellationToken token)
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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, LimboLogs.Instance);

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
            JustFirst = 8,
            AllKnown = 16,
            TimeoutOnFullBatch = 32,
            NoBody = 64,
        }
    }
}