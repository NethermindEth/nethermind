//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
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

        private class SyncPeerMock : ISyncPeer
        {
            private readonly bool _withReceipts;
            private readonly BlockHeadersMessageSerializer _headersSerializer = new BlockHeadersMessageSerializer();
            private readonly BlockBodiesMessageSerializer _bodiesSerializer = new BlockBodiesMessageSerializer();
            private readonly ReceiptsMessageSerializer _receiptsSerializer = new ReceiptsMessageSerializer(RopstenSpecProvider.Instance);

            private IDb _blockInfoDb = new MemDb();
            public BlockTree BlockTree { get; private set; }
            private IReceiptStorage _receiptStorage = new InMemoryReceiptStorage();

            public Response Flags { get; set; }

            public SyncPeerMock(long chainLength, bool withReceipts, Response flags)
            {
                _withReceipts = withReceipts;
                Flags = flags;
                BuildTree(chainLength, withReceipts);
            }

            private void BuildTree(long chainLength, bool withReceipts)
            {
                _receiptStorage = new InMemoryReceiptStorage();
                var builder = Build.A.BlockTree();
                if (withReceipts)
                {
                    builder = builder.WithTransactions(_receiptStorage, MainNetSpecProvider.Instance);
                }

                builder = builder.OfChainLength((int) chainLength);
                BlockTree = builder.TestObject;
            }

            public void ExtendTree(long newLength)
            {
                BuildTree(newLength, _withReceipts);
            }

            public Guid SessionId { get; }
            public bool IsFastSyncSupported { get; }
            public Node Node { get; }
            public string ClientId { get; }
            public UInt256 TotalDifficultyOnSessionStart { get; }

            public void Disconnect(DisconnectReason reason, string details)
            {
                throw new NotImplementedException();
            }

            public async Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
            {
                bool consistent = Flags.HasFlag(Response.Consistent);
                bool justFirst = Flags.HasFlag(Response.JustFirst);
                bool allKnown = Flags.HasFlag(Response.AllKnown);
                bool noBody = Flags.HasFlag(Response.NoBody);

                BlockBody[] headers = new BlockBody[blockHashes.Count];
                int i = 0;
                foreach (Keccak blockHash in blockHashes)
                {
                    headers[i++] = BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.None).Body;
                }

                BlockBodiesMessage message = new BlockBodiesMessage(headers);
                byte[] messageSerialized = _bodiesSerializer.Serialize(message);
                return await Task.FromResult(_bodiesSerializer.Deserialize(messageSerialized).Bodies);
            }

            public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public async Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
            {
                bool consistent = Flags.HasFlag(Response.Consistent);
                bool justFirst = Flags.HasFlag(Response.JustFirst);
                bool allKnown = Flags.HasFlag(Response.AllKnown);
                bool timeoutOnFullBatch = Flags.HasFlag(Response.TimeoutOnFullBatch);
                bool noBody = Flags.HasFlag(Response.NoBody);

                if (timeoutOnFullBatch && number == SyncBatchSize.Max)
                {
                    throw new TimeoutException();
                }

                BlockHeader[] headers = new BlockHeader[maxBlocks];
                for (int i = 0; i < (justFirst ? 1 : maxBlocks); i++)
                {
                    headers[i] = BlockTree.FindHeader(number + i, BlockTreeLookupOptions.None);
                }

                BlockHeadersMessage message = new BlockHeadersMessage(headers);
                byte[] messageSerialized = _headersSerializer.Serialize(message);
                return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
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

            public async Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                TxReceipt[][] receipts = new TxReceipt[blockHash.Count][];
                int i = 0;
                foreach (Keccak keccak in blockHash)
                {
                    Block block = BlockTree.FindBlock(keccak, BlockTreeLookupOptions.None);
                    TxReceipt[] blockReceipts = new TxReceipt[block.Transactions.Length];

                    int receiptIndex = 0;
                    foreach (Transaction transaction in block.Transactions)
                    {
                        blockReceipts[receiptIndex++] = _receiptStorage.Find(transaction.Hash);
                    }

                    receipts[i++] = blockReceipts;
                }

                ReceiptsMessage message = new ReceiptsMessage(receipts);
                byte[] messageSerialized = _receiptsSerializer.Serialize(message);
                return await Task.FromResult(_receiptsSerializer.Deserialize(messageSerialized).TxReceipts);
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }
        }

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
                        Keccak receiptRoot = i == 1 ? Keccak.EmptyTreeHash : new Keccak("0x9904791428367d3f36f2be68daf170039dd0b3d6b23da00697de816a05fb5cc1");
                        headers[i] = consistent
                            ? Build.A.BlockHeader.WithReceiptsRoot(receiptRoot).WithParent(headers[i - 1]).WithOmmersHash(noBody ? Keccak.OfAnEmptySequenceRlp : Keccak.Zero).TestObject
                            : Build.A.BlockHeader.WithReceiptsRoot(receiptRoot).WithNumber(headers[i - 1].Number + 1).TestObject;

                        if (allKnown)
                        {
                            _blockTree.SuggestHeader(headers[i]);
                        }

                        _testHeaderMapping[startNumber + i] = headers[i].Hash;
                    }
                }

                foreach (BlockHeader header in headers)
                {
                    _headers[header.Hash] = header;
                }

                BlockHeadersMessage message = new BlockHeadersMessage(headers);
                byte[] messageSerialized = _headersSerializer.Serialize(message);
                return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
            }

            private readonly BlockHeadersMessageSerializer _headersSerializer = new BlockHeadersMessageSerializer();
            private readonly BlockBodiesMessageSerializer _bodiesSerializer = new BlockBodiesMessageSerializer();
            private readonly ReceiptsMessageSerializer _receiptsSerializer = new ReceiptsMessageSerializer(RopstenSpecProvider.Instance);

            private Dictionary<Keccak, BlockHeader> _headers = new Dictionary<Keccak, BlockHeader>();
            private Dictionary<Keccak, BlockBody> _bodies = new Dictionary<Keccak, BlockBody>();

            public async Task<BlockBody[]> BuildBlocksResponse(IList<Keccak> blockHashes, Response flags)
            {
                bool consistent = flags.HasFlag(Response.Consistent);
                bool justFirst = flags.HasFlag(Response.JustFirst);
                bool allKnown = flags.HasFlag(Response.AllKnown);
                bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
                bool withTransactions = flags.HasFlag(Response.WithTransactions);

                if (timeoutOnFullBatch && blockHashes.Count == SyncBatchSize.Max)
                {
                    throw new TimeoutException();
                }

                BlockHeader startHeader = _blockTree.FindHeader(blockHashes[0], BlockTreeLookupOptions.None);
                if (startHeader == null) startHeader = _headers[blockHashes[0]];

                BlockHeader[] blockHeaders = new BlockHeader[blockHashes.Count];
                BlockBody[] blockBodies = new BlockBody[blockHashes.Count];
                blockBodies[0] = new BlockBody(new Transaction[0], new BlockHeader[0]);
                blockHeaders[0] = startHeader;

                _bodies[startHeader.Hash] = blockBodies[0];
                _headers[startHeader.Hash] = blockHeaders[0];
                if (!justFirst)
                {
                    for (int i = 0; i < blockHashes.Count; i++)
                    {
                        blockHeaders[i] = consistent
                            ? _headers[blockHashes[i]]
                            : Build.A.BlockHeader.WithNumber(blockHeaders[i - 1].Number + 1).WithHash(blockHashes[i]).TestObject;

                        _testHeaderMapping[startHeader.Number + i] = blockHeaders[i].Hash;

                        BlockHeader header = consistent
                            ? blockHeaders[i]
                            : blockHeaders[i - 1];

                        BlockBuilder blockBuilder = Build.A.Block.WithHeader(header);

                        if (withTransactions && header.ReceiptsRoot != Keccak.EmptyTreeHash)
                        {
                            blockBuilder.WithTransactions(Build.A.Transaction.WithValue(i * 2).SignedAndResolved().TestObject,
                                Build.A.Transaction.WithValue(i * 2 + 1).SignedAndResolved().TestObject);
                        }

                        Block block = blockBuilder.TestObject;
                        blockBodies[i] = new BlockBody(block.Transactions, block.Ommers);
                        _bodies[blockHashes[i]] = blockBodies[i];

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

            public async Task<TxReceipt[][]> BuildReceiptsResponse(IList<Keccak> blockHashes, Response flags = Response.AllCorrect)
            {
                TxReceipt[][] receipts = new TxReceipt[blockHashes.Count][];
                for (int i = 0; i < receipts.Length; i++)
                {
                    BlockBody body = _bodies[blockHashes[i]];
                    receipts[i] = body.Transactions
                        .Select(t => Build.A.Receipt
                            .WithStatusCode(StatusCode.Success)
                            .WithGasUsed(10)
                            .WithBloom(Bloom.Empty)
                            .WithLogs(Build.A.LogEntry.WithAddress(t.SenderAddress).WithTopics(TestItem.KeccakA).TestObject)
                            .TestObject)
                        .ToArray();

                    _headers[blockHashes[i]].ReceiptsRoot = flags.HasFlag(Response.IncorrectReceiptRoot)
                        ? Keccak.EmptyTreeHash
                        : BlockExtensions.CalculateReceiptRoot(_headers[blockHashes[i]].Number, MainNetSpecProvider.Instance, receipts[i]);
                }

                ReceiptsMessage message = new ReceiptsMessage(receipts);
                byte[] messageSerialized = _receiptsSerializer.Serialize(message);
                return await Task.FromResult(_receiptsSerializer.Deserialize(messageSerialized).TxReceipts);
            }
        }

        [SetUp]
        public void Setup()
        {
            Block genesis = Build.A.Block.Genesis.TestObject;
            MemDb blockInfoDb = new MemDb();
            _blockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            _blockTree.SuggestBlock(genesis);

            _testHeaderMapping = new Dictionary<long, Keccak>();
            _testHeaderMapping.Add(0, genesis.Hash);

            _responseBuilder = new ResponseBuilder(_blockTree, _testHeaderMapping);
        }

        [TestCase(1L, BlockDownloaderOptions.DownloadAndProcess)]
        [TestCase(32L, BlockDownloaderOptions.DownloadAndProcess)]
        [TestCase(32L, BlockDownloaderOptions.Download)]
        [TestCase(1L, BlockDownloaderOptions.DownloadWithReceipts)]
        [TestCase(2L, BlockDownloaderOptions.DownloadWithReceipts)]
        [TestCase(3L, BlockDownloaderOptions.DownloadWithReceipts)]
        [TestCase(32L, BlockDownloaderOptions.DownloadWithReceipts)]
        [TestCase(SyncBatchSize.Max * 8, BlockDownloaderOptions.DownloadWithReceipts)]
        [TestCase(SyncBatchSize.Max * 8, BlockDownloaderOptions.DownloadAndProcess)]
        public async Task Happy_path(long headNumber, int options)
        {
            BlockDownloaderOptions blockDownloaderOptions = (BlockDownloaderOptions) options;
            bool withReceipts = blockDownloaderOptions == BlockDownloaderOptions.DownloadWithReceipts;
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            Response blockResponseOptions = Response.AllCorrect;
            if (withReceipts)
            {
                blockResponseOptions |= Response.WithTransactions;
            }

            SyncPeerMock syncPeer = new SyncPeerMock(headNumber, withReceipts, blockResponseOptions);

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = headNumber;

            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            Assert.AreEqual(Math.Max(0, headNumber - SyncModeSelector.FullSyncThreshold), _blockTree.BestSuggestedHeader.Number, "headers");

            peerInfo.HeadNumber *= 2;
            syncPeer.ExtendTree(peerInfo.HeadNumber + 1);
            await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None, blockDownloaderOptions);
            _blockTree.BestSuggestedHeader.Number.Should().Be(Math.Max(0, peerInfo.HeadNumber));
            _blockTree.IsMainChain(_blockTree.BestSuggestedHeader.Hash).Should().Be(blockDownloaderOptions != BlockDownloaderOptions.DownloadAndProcess);

            int receiptCount = 0;
            for (int i = (int) Math.Max(0, (headNumber - SyncModeSelector.FullSyncThreshold)); i < peerInfo.HeadNumber; i++)
            {
                if (i % 3 == 0)
                {
                    receiptCount += 2;
                }
            }

            inMemoryReceiptStorage.Count.Should().Be(withReceipts ? receiptCount : 0);
        }
        
        [Test]
        public async Task Ancestor_lookup_simple()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            
            _blockTree = Build.A.BlockTree().OfChainLength(1024).TestObject;
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            Response blockResponseOptions = Response.AllCorrect;
            SyncPeerMock syncPeer = new SyncPeerMock(2048 + 1, false, blockResponseOptions);

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 2048;

            var block1024 = Build.A.Block.WithParent(_blockTree.Head).WithDifficulty(_blockTree.Head.Difficulty + 1).TestObject;
            var block1025 = Build.A.Block.WithParent(block1024).WithDifficulty(block1024.Difficulty + 1).TestObject;
            var block1026 = Build.A.Block.WithParent(block1025).WithDifficulty(block1025.Difficulty + 1).TestObject;
            _blockTree.SuggestBlock(block1024);
            _blockTree.SuggestBlock(block1025);
            _blockTree.SuggestBlock(block1026);

            for (int i = 0; i < 1023; i++)
            {
                Assert.AreEqual(_blockTree.FindBlock(i, BlockTreeLookupOptions.None).Hash, syncPeer.BlockTree.FindBlock(i, BlockTreeLookupOptions.None).Hash, i.ToString());
            }

            await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None, BlockDownloaderOptions.DownloadWithReceipts);
            _blockTree.BestSuggestedHeader.Number.Should().Be(peerInfo.HeadNumber);
            _blockTree.IsMainChain(_blockTree.BestSuggestedHeader.Hash).Should().Be(true);
        }
        
        [Test]
        public async Task Ancestor_lookup_headers()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            
            _blockTree = Build.A.BlockTree().OfChainLength(1024).TestObject;
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            Response blockResponseOptions = Response.AllCorrect;
            SyncPeerMock syncPeer = new SyncPeerMock(2048 + 1, false, blockResponseOptions);

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 2048;

            var block1024 = Build.A.Block.WithParent(_blockTree.Head).WithDifficulty(_blockTree.Head.Difficulty + 1).TestObject;
            var block1025 = Build.A.Block.WithParent(block1024).WithDifficulty(block1024.Difficulty + 1).TestObject;
            var block1026 = Build.A.Block.WithParent(block1025).WithDifficulty(block1025.Difficulty + 1).TestObject;
            _blockTree.SuggestBlock(block1024);
            _blockTree.SuggestBlock(block1025);
            _blockTree.SuggestBlock(block1026);

            for (int i = 0; i < 1023; i++)
            {
                Assert.AreEqual(_blockTree.FindBlock(i, BlockTreeLookupOptions.None).Hash, syncPeer.BlockTree.FindBlock(i, BlockTreeLookupOptions.None).Hash, i.ToString());
            }

            await blockDownloader.DownloadHeaders(peerInfo, 0, CancellationToken.None);
            _blockTree.BestSuggestedHeader.Number.Should().Be(peerInfo.HeadNumber);
        }
        
        [Test]
        public void Ancestor_failure()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            
            _blockTree = Build.A.BlockTree().OfChainLength(2048 + 1).TestObject;
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            Response blockResponseOptions = Response.AllCorrect;
            SyncPeerMock syncPeer = new SyncPeerMock(2072 + 1, true, blockResponseOptions);

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 2048;
            
            Assert.ThrowsAsync<EthSynchronizationException>(() => blockDownloader.DownloadHeaders(peerInfo, 0, CancellationToken.None));
            _blockTree.BestSuggestedHeader.Number.Should().Be(2048);
        }
        
        [Test]
        public void Ancestor_failure_blocks()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            
            _blockTree = Build.A.BlockTree().OfChainLength(2048 + 1).TestObject;
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            Response blockResponseOptions = Response.AllCorrect;
            SyncPeerMock syncPeer = new SyncPeerMock(2072 + 1, true, blockResponseOptions);

            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 2048;
            
            Assert.ThrowsAsync<EthSynchronizationException>(() => blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None));
            _blockTree.BestSuggestedHeader.Number.Should().Be(2048);
        }

        [Test]
        public async Task Can_sync_with_peer_when_it_times_out_on_full_batch()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async ci => await _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.TimeoutOnFullBatch));

            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.TimeoutOnFullBatch));

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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.AllKnown));

            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.AllKnown));

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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.JustFirst));

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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.NoBody));

            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.JustFirst));

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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);
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

            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);
            Task task = blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);
            await task.ContinueWith(t => Assert.True(t.IsFaulted));
        }

        [Test]
        public async Task Throws_on_invalid_seal()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.NeverValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.NeverValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

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
            public void HintValidationRange(Guid guid, long start, long end)
            {
            }

            public bool ValidateParams(BlockHeader parent, BlockHeader header)
            {
                Thread.Sleep(1000);
                return true;
            }

            public bool ValidateSeal(BlockHeader header, bool force)
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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, new SlowSealValidator(), NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect));

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
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, new SlowHeaderValidator(), TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect));

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

            public Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
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

            public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }
        }

        [Test]
        public async Task Faults_on_get_headers_faulting()
        {
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, new InMemoryReceiptStorage(), RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = new ThrowingPeer();
            PeerInfo peerInfo = new PeerInfo(syncPeer);
            peerInfo.TotalDifficulty = UInt256.MaxValue;
            peerInfo.HeadNumber = 1000;

            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None)
                .ContinueWith(t => Assert.True(t.IsFaulted));
        }

        [Test]
        public async Task Throws_on_block_task_exception()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            Task<BlockHeader[]> buildHeadersResponse = null;
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildHeadersResponse = _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<BlockBody[]>(new TimeoutException()));

            syncPeer.GetReceipts(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions));

            PeerInfo peerInfo = new PeerInfo(syncPeer) {TotalDifficulty = UInt256.MaxValue, HeadNumber = 1};
            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);

            peerInfo.HeadNumber *= 2;

            Func<Task> action = async () => await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None, BlockDownloaderOptions.Download);
            action.Should().Throw<EthSynchronizationException>().WithInnerException<AggregateException>().WithInnerException<TimeoutException>();
        }

        [TestCase(BlockDownloaderOptions.DownloadWithReceipts, true)]
        [TestCase(BlockDownloaderOptions.Download, false)]
        [TestCase(BlockDownloaderOptions.DownloadAndProcess, false)]
        public async Task Throws_on_receipt_task_exception_when_downloading_receipts(int options, bool shouldThrow)
        {
            BlockDownloaderOptions blockDownloaderOptions = (BlockDownloaderOptions) options;
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            Task<BlockHeader[]> buildHeadersResponse = null;
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildHeadersResponse = _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            Task<BlockBody[]> buildBlocksResponse = null;
            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildBlocksResponse = _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions));

            syncPeer.GetReceipts(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<TxReceipt[][]>(new TimeoutException()));

            PeerInfo peerInfo = new PeerInfo(syncPeer) {TotalDifficulty = UInt256.MaxValue, HeadNumber = 1};
            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);

            peerInfo.HeadNumber *= 2;

            Func<Task> action = async () => await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None, blockDownloaderOptions);
            if (shouldThrow)
            {
                action.Should().Throw<EthSynchronizationException>().WithInnerException<AggregateException>().WithInnerException<TimeoutException>();
            }
            else
            {
                action.Should().NotThrow();
            }
        }

        [Test]
        public async Task Throws_on_block_bodies_count_higher_than_receipts_list_count()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            Task<BlockHeader[]> buildHeadersResponse = null;
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildHeadersResponse = _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            Task<BlockBody[]> buildBlocksResponse = null;
            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildBlocksResponse = _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions));

            syncPeer.GetReceipts(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions).Result.Skip(1).ToArray());

            PeerInfo peerInfo = new PeerInfo(syncPeer) {TotalDifficulty = UInt256.MaxValue, HeadNumber = 1};
            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);

            peerInfo.HeadNumber *= 2;

            Func<Task> action = async () => await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None, BlockDownloaderOptions.DownloadWithReceipts);
            action.Should().Throw<EthSynchronizationException>();
        }

        [Test]
        public async Task Throws_on_transaction_count_different_than_receipts_count_in_block()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            Task<BlockHeader[]> buildHeadersResponse = null;
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildHeadersResponse = _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

            Task<BlockBody[]> buildBlocksResponse = null;
            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildBlocksResponse = _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions));

            syncPeer.GetReceipts(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions)
                    .Result.Select(r => r == null || r.Length == 0 ? r : r.Skip(1).ToArray()).ToArray());

            PeerInfo peerInfo = new PeerInfo(syncPeer) {TotalDifficulty = UInt256.MaxValue, HeadNumber = 1};
            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);

            peerInfo.HeadNumber *= 2;

            Func<Task> action = async () => await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None, BlockDownloaderOptions.DownloadWithReceipts);
            action.Should().Throw<EthSynchronizationException>();
        }

        [Test]
        public async Task Throws_on_incorrect_receipts_root()
        {
            InMemoryReceiptStorage inMemoryReceiptStorage = new InMemoryReceiptStorage();
            BlockDownloader blockDownloader = new BlockDownloader(_blockTree, TestBlockValidator.AlwaysValid, TestSealValidator.AlwaysValid, NullSyncReport.Instance, inMemoryReceiptStorage, RopstenSpecProvider.Instance, LimboLogs.Instance);

            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            Task<BlockHeader[]> buildHeadersResponse = null;
            syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildHeadersResponse = _responseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.IncorrectReceiptRoot));

            Task<BlockBody[]> buildBlocksResponse = null;
            syncPeer.GetBlockBodies(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => buildBlocksResponse = _responseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions));

            syncPeer.GetReceipts(Arg.Any<IList<Keccak>>(), Arg.Any<CancellationToken>())
                .Returns(ci => _responseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Keccak>>(0), Response.AllCorrect | Response.WithTransactions).Result);

            PeerInfo peerInfo = new PeerInfo(syncPeer) {TotalDifficulty = UInt256.MaxValue, HeadNumber = 1};
            await blockDownloader.DownloadHeaders(peerInfo, SyncModeSelector.FullSyncThreshold, CancellationToken.None);

            peerInfo.HeadNumber *= 2;

            Func<Task> action = async () => await blockDownloader.DownloadBlocks(peerInfo, 0, CancellationToken.None, BlockDownloaderOptions.DownloadWithReceipts);
            action.Should().Throw<EthSynchronizationException>();
        }

        [Flags]
        private enum Response
        {
            Consistent = 1,
            AllCorrect = 7,
            JustFirst = 8,
            AllKnown = 16,
            TimeoutOnFullBatch = 32,
            NoBody = 64,
            WithTransactions = 128,
            IncorrectReceiptRoot = 256
        }
    }
}