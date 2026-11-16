// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
public class Eth69ReceiptsServingBenchmarks
{
    private const int RequestedBlocks = 16;

    private TxReceipt[][] _blocks = null!;
    private ReceiptsServingHandler _handler = null!;
    private GetReceiptsMessage _request = null!;

    [Params(100, 200)]
    public int ReceiptsPerBlock { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        Random random = new(42);
        _blocks = new TxReceipt[RequestedBlocks][];
        Dictionary<Hash256, TxReceipt[]> receipts = [with(RequestedBlocks)];
        ArrayPoolList<Hash256> hashes = [with(RequestedBlocks)];
        for (int i = 0; i < RequestedBlocks; i++)
        {
            _blocks[i] = CreateBlockReceipts(random, 21_000_000 + (ulong)i, ReceiptsPerBlock);
            Hash256 hash = Keccak.Compute(BitConverter.GetBytes(i));
            receipts[hash] = _blocks[i];
            hashes.Add(hash);
        }

        _request = new GetReceiptsMessage(hashes);
        _handler = new ReceiptsServingHandler(new ReceiptsSyncServer(receipts));
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _request.Dispose();
        _handler.Dispose();
    }

    [Benchmark]
    public int Serve()
    {
        ClearBlooms();
        using ReceiptsMessage response = _handler.Serve(_request).Result;
        return response.TxReceipts.Count;
    }

    private void ClearBlooms()
    {
        foreach (TxReceipt[] block in _blocks)
        {
            foreach (TxReceipt receipt in block)
            {
                receipt.Bloom = null;
            }
        }
    }

    private static TxReceipt[] CreateBlockReceipts(Random random, ulong blockNumber, int count)
    {
        TxReceipt[] receipts = new TxReceipt[count];
        ulong gasUsed = 0;
        for (int i = 0; i < count; i++)
        {
            LogEntry[] logs = new LogEntry[random.Next(10) < 4 ? 0 : random.Next(1, 7)];
            for (int j = 0; j < logs.Length; j++)
            {
                Hash256[] topics = new Hash256[random.Next(1, 5)];
                for (int k = 0; k < topics.Length; k++)
                {
                    byte[] topic = new byte[Hash256.Size];
                    random.NextBytes(topic);
                    topics[k] = new Hash256(topic);
                }

                byte[] data = new byte[32 * random.Next(0, 4)];
                random.NextBytes(data);
                logs[j] = new LogEntry(TestItem.Addresses[random.Next(TestItem.Addresses.Length)], data, topics);
            }

            gasUsed += (ulong)random.Next(21_000, 300_000);
            receipts[i] = new TxReceipt { TxType = TxType.EIP1559, StatusCode = 1, GasUsedTotal = gasUsed, BlockNumber = blockNumber, Logs = logs };
        }

        return receipts;
    }

    private static ISession CreateSession()
    {
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Loopback, 30303)));
        return session;
    }

    private sealed class ReceiptsServingHandler(ISyncServer syncServer) : Eth69ProtocolHandler(
        CreateSession(),
        Substitute.For<IMessageSerializationService>(),
        new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
        syncServer,
        Substitute.For<IBackgroundTaskScheduler>(),
        Substitute.For<ITxPool>(),
        Substitute.For<IGossipPolicy>(),
        Substitute.For<IForkInfo>(),
        LimboLogs.Instance,
        Substitute.For<ITxPoolConfig>(),
        Substitute.For<IChainHeadSpecProvider>())
    {
        public Task<ReceiptsMessage> Serve(GetReceiptsMessage request) => FulfillReceiptsRequest(request, CancellationToken.None);
    }

    private sealed class ReceiptsSyncServer(Dictionary<Hash256, TxReceipt[]> receipts) : ISyncServer
    {
        public TxReceipt[] GetReceipts(Hash256 blockHashes) => receipts.GetValueOrDefault(blockHashes);

        public bool TryAppendReceipts(Hash256 blockHash, IRlpDecoder<TxReceipt> decoder, bool withBloom, ArrayPoolList<byte> destination)
        {
            if (!receipts.TryGetValue(blockHash, out TxReceipt[] blockReceipts)) return false;

            int contentLength = 0;
            foreach (TxReceipt receipt in blockReceipts) contentLength += decoder.GetLength(receipt, RlpBehaviors.Eip658Receipts);
            RlpWriter writer = RlpWriter.ContentWriter(destination.AddUninitialized(Rlp.LengthOfSequence(contentLength)));
            RlpSequence sequence = writer.StartSequence(contentLength);
            foreach (TxReceipt receipt in blockReceipts) decoder.Encode(ref writer, receipt, RlpBehaviors.Eip658Receipts);
            writer.EndSequence(sequence);
            return true;
        }

        public void HintBlock(Hash256 hash, ulong number, ISyncPeer receivedFrom) { }
        public void AddNewBlock(Block block, ISyncPeer node) { }
        public void StopNotifyingPeersAboutNewBlocks() { }
        public MemoryManager<byte> GetBlockAccessListRlp(Hash256 blockHash) => null;
        public MemoryManager<byte> FindBlockRlp(Hash256 hash) => null;
        public BlockHeader FindHeader(Hash256 hash) => null;
        public Hash256 FindHash(ulong number) => null;
        public IOwnedReadOnlyList<byte> FindHeadersRlp(Hash256 hash, int numberOfBlocks, int skip, bool reverse) => throw new NotSupportedException();
        public IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken, NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State) => throw new NotSupportedException();
        public int GetPeerCount() => 0;
        public ulong NetworkId => 1;
        public BlockHeader Genesis => throw new NotSupportedException();
        public BlockHeader Head => null;
        public ulong LowestBlock => 0;
        public void Dispose() { }
    }
}
