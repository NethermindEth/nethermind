// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-7975 - partial block receipt lists.
/// </summary>
public class Eth70ProtocolHandler : Eth69ProtocolHandler, IStaticProtocolInfo
{
    private static readonly ReceiptMessageDecoder69 ReceiptMessageDecoder = new();
    private static readonly Func<ReceiptsMessage70, int, ReceiptsMessage70> SelectReceipts =
        static (message, _) => message;
    private static readonly Action<ReadOnlySpan<byte>, GetReceiptsMessage70> ValidateReceipts =
        static (content, request) => ReceiptsResponseBudget.ThrowIfExceeded(content, 2, request.RequestedBlocks, request.MaxReceiptsPerBlock.Span, request.FirstBlockReceiptIndex);

    /// <summary>Sentinel for <c>lastBlockNumber</c> meaning no receipt block has been seen yet.</summary>
    private const ulong NoBlockSeen = ulong.MaxValue;
    // Before EIP-3529, refunds could reduce gas used by at most half.
    private const ulong MinimumSupportedTransactionGas = GasCostOf.Transaction / 2;

    private readonly MessageDictionary<GetReceiptsMessage70, ReceiptsMessage70> _receiptsRequests70;
    private readonly ISpecProvider _specProvider;

    public Eth70ProtocolHandler(
        ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ITxPool txPool,
        IGossipPolicy gossipPolicy,
        IForkInfo forkInfo,
        ILogManager logManager,
        ITxPoolConfig txPoolConfig,
        IChainHeadSpecProvider specProvider,
        ITxGossipPolicy? transactionsGossipPolicy = null)
        : base(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool,
            gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, transactionsGossipPolicy)
    {
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _receiptsRequests70 = new MessageDictionary<GetReceiptsMessage70, ReceiptsMessage70>(this);
    }

    public override string Name => "eth70";

    public new static byte Version => EthVersions.Eth70;
    public override byte ProtocolVersion => Version;

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.Length;
        switch (message.PacketType)
        {
            case Eth70MessageCode.Receipts:
                HandleCorrelatedResponse(_receiptsRequests70, message, size, SelectReceipts, ValidateReceipts);
                return true;
            case Eth70MessageCode.GetReceipts:
                HandleInBackground<GetReceiptsMessage70, ReceiptsMessage70>(message, Handle);
                return true;
            default:
                return base.HandleMessageCore(message);
        }
    }

    internal ValueTask<ReceiptsMessage70> Handle(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        using GetReceiptsMessage70 message = getReceiptsMessage;
        ReceiptsResponse response = FulfillReceiptsRequest(message, cancellationToken);
        return ValueTask.FromResult(new ReceiptsMessage70(message.RequestId, response.TxReceipts, response.LastBlockIncomplete));
    }

    private ReceiptsResponse FulfillReceiptsRequest(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        ReadOnlySpan<Hash256> hashes = getReceiptsMessage.Hashes.AsSpan();
        ArrayPoolList<TxReceipt[]> txReceipts = new(Math.Min(hashes.Length, MaxReceiptsLookups));
        bool lastBlockIncomplete = false;

        try
        {
            ulong responseReceiptsContentSize = 0;
            bool hasNonEmptyReceiptBlock = false;
            for (int blockIndex = 0; blockIndex < hashes.Length && blockIndex < MaxReceiptsLookups; blockIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Hash256 blockHash = hashes[blockIndex];
                TxReceipt[]? receipts = SyncServer.GetReceipts(blockHash);
                if (receipts is null)
                {
                    break;
                }

                long requestedStartIndex = blockIndex == 0 ? getReceiptsMessage.FirstBlockReceiptIndex : 0;
                if (requestedStartIndex < 0 || requestedStartIndex > receipts.Length)
                {
                    throw new SubprotocolException($"Invalid firstBlockReceiptIndex {requestedStartIndex} for block receipts length {receipts.Length}");
                }

                int startIndex = (int)requestedStartIndex;

                if (receipts.Length == 0)
                {
                    ulong emptyBlockSize = GetBlockReceiptsSize(0);
                    ulong responseSize = GetEth70ReceiptsResponseSize(
                        responseReceiptsContentSize + emptyBlockSize,
                        getReceiptsMessage.RequestId);
                    if (responseSize > SoftOutgoingMessageSizeLimit && responseReceiptsContentSize > 0)
                    {
                        break;
                    }

                    if (startIndex != 0)
                    {
                        throw new SubprotocolException($"Invalid firstBlockReceiptIndex {startIndex} for empty receipts block");
                    }

                    txReceipts.Add([]);
                    responseReceiptsContentSize += emptyBlockSize;
                    continue;
                }

                if (startIndex == receipts.Length)
                {
                    ulong emptyBlockSize = GetBlockReceiptsSize(0);
                    ulong responseSize = GetEth70ReceiptsResponseSize(
                        responseReceiptsContentSize + emptyBlockSize,
                        getReceiptsMessage.RequestId);
                    if (responseSize > SoftOutgoingMessageSizeLimit && responseReceiptsContentSize > 0)
                    {
                        break;
                    }

                    txReceipts.Add([]);
                    responseReceiptsContentSize += emptyBlockSize;
                    continue;
                }

                ulong remainingBlockSize = GetBlockReceiptsSize(receipts, startIndex);
                if (GetEth70ReceiptsResponseSize(
                    responseReceiptsContentSize + remainingBlockSize,
                    getReceiptsMessage.RequestId) <= SoftOutgoingMessageSizeLimit)
                {
                    txReceipts.Add(CopyReceipts(receipts, startIndex, receipts.Length - startIndex));
                    responseReceiptsContentSize += remainingBlockSize;
                    hasNonEmptyReceiptBlock = true;
                    continue;
                }

                if (hasNonEmptyReceiptBlock)
                {
                    break;
                }

                if (GetEth70ReceiptsResponseSize(responseReceiptsContentSize + remainingBlockSize, getReceiptsMessage.RequestId) <= HardOutgoingReceiptsMessageSizeLimit)
                {
                    txReceipts.Add(CopyReceipts(receipts, startIndex, receipts.Length - startIndex));
                    hasNonEmptyReceiptBlock = true;
                    break;
                }

                ulong blockReceiptsContentSize = 0;
                int taken = 0;
                ulong lastBlockNumber = NoBlockSeen;
                RlpBehaviors behaviors = RlpBehaviors.None;
                for (int receiptIndex = startIndex; receiptIndex < receipts.Length; receiptIndex++)
                {
                    ulong receiptSize = GetReceiptSize(receipts[receiptIndex], ref lastBlockNumber, ref behaviors);
                    ulong nextBlockReceiptsContentSize = blockReceiptsContentSize + receiptSize;
                    ulong nextBlockSize = GetBlockReceiptsSize(nextBlockReceiptsContentSize);
                    if (GetEth70ReceiptsResponseSize(responseReceiptsContentSize + nextBlockSize, getReceiptsMessage.RequestId) > HardOutgoingReceiptsMessageSizeLimit)
                    {
                        break;
                    }

                    taken++;
                    blockReceiptsContentSize = nextBlockReceiptsContentSize;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (taken == 0)
                {
                    throw new SubprotocolException($"Single receipt exceeds hard eth/70 receipts response size limit ({HardOutgoingReceiptsMessageSizeLimit} bytes)");
                }

                lastBlockIncomplete = startIndex + taken < receipts.Length;
                txReceipts.Add(CopyReceipts(receipts, startIndex, taken));
                hasNonEmptyReceiptBlock = true;
                break;
            }

            return new ReceiptsResponse(txReceipts, lastBlockIncomplete);
        }
        catch
        {
            txReceipts.Dispose();
            throw;
        }
    }

    public override async Task<IOwnedReadOnlyList<TxReceipt[]>> GetReceipts(IReadOnlyList<Hash256> blockHashes, ReadOnlyMemory<int> expectedReceiptCounts, CancellationToken token)
    {
        if (blockHashes.Count == 0)
        {
            return ArrayPoolList<TxReceipt[]>.Empty();
        }

        return await _nodeStats.RunSizeAndLatencyRequestSizer<IOwnedReadOnlyList<TxReceipt[]>, Hash256, TxReceipt[]>(
            RequestType.Receipts,
            blockHashes,
            async clampedHashes =>
            {
                using ArrayPoolList<Hash256> ownedHashes = clampedHashes.ToPooledList();
                return await SendGetReceiptsWithPaging(ownedHashes, expectedReceiptCounts, token);
            });
    }

    /// <param name="blockHashes">The blocks to get the receipts of.</param>
    /// <param name="expectedReceiptCounts">
    /// Per block, from the caller, its transaction count or a negative value when unknown; blocks without a count
    /// are bounded by the transactions of the local block body when there is one.
    /// </param>
    /// <param name="token">Cancels the requests.</param>
    private async Task<(IOwnedReadOnlyList<TxReceipt[]>, long)> SendGetReceiptsWithPaging(IOwnedReadOnlyList<Hash256> blockHashes, ReadOnlyMemory<int> expectedReceiptCounts, CancellationToken token)
    {
        using ReceiptAggregationBuilder aggregated = new(blockHashes.Count);
        ArrayPoolList<TxReceipt>? partialReceipts = null;
        ulong partialReceiptsGas = 0;
        ulong partialReceiptsLogsGas = 0;
        ulong partialReceiptsContentSize = 0;
        int blockIndex = 0;
        int firstBlockReceiptIndex = 0;
        ulong totalResponseSize = 0;

        using ArrayPoolList<BlockReceiptValidationContext> validationContexts = new(blockHashes.Count);
        foreach (Hash256 blockHash in blockHashes.AsSpan())
        {
            BlockHeader? header = SyncServer.FindHeader(blockHash);
            if (header is null)
            {
                // Without a timestamp the EIP-8116 rule is unknown; per-transaction checks accept both forms.
                validationContexts.Add(new(0, null, null, RlpBehaviors.None,
                    ValidateGasUpperBound: false, ValidateGasEqual: false, IsEip8116Enabled: _specProvider.GetFinalSpec().IsEip8116Enabled));
                continue;
            }

            IReleaseSpec spec = _specProvider.GetSpec(header);
            ulong[]? transactionGasLimits = SyncServer.FindBlockRlp(blockHash) is { } blockRlp
                ? BlockBodyDecoder.ReadTransactionGasLimits(blockRlp)
                : null;
            validationContexts.Add(new(header.GasUsed, header.GasLimit, transactionGasLimits,
                spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None,
                ValidateGasUpperBound: !spec.IsEip8037Enabled,
                ValidateGasEqual: !spec.IsEip7778Enabled && !spec.IsEip8037Enabled,
                IsEip8116Enabled: spec.IsEip8116Enabled));
        }

        int[] maxReceiptsPerBlock = new int[blockHashes.Count];
        ReadOnlySpan<int> expectedCounts = expectedReceiptCounts.Span;
        for (int i = 0; i < blockHashes.Count; i++)
        {
            int expected = i < expectedCounts.Length ? expectedCounts[i] : -1;
            maxReceiptsPerBlock[i] = expected >= 0 ? expected : validationContexts[i].TransactionGasLimits?.Length ?? -1;
        }

        try
        {
            while (blockIndex < blockHashes.Count)
            {
                using GetReceiptsMessage70 request = BuildRequest(blockHashes, blockIndex, firstBlockReceiptIndex, maxReceiptsPerBlock.AsMemory());
                (ReceiptsMessage70 response, ulong size) = await SendRequest(request, token);

                using (response)
                {
                    totalResponseSize += size;

                    if (response.TxReceipts.Count == 0)
                    {
                        if (response.LastBlockIncomplete || firstBlockReceiptIndex > 0)
                        {
                            throw new SubprotocolException("Peer returned no progress for partial receipts");
                        }

                        break;
                    }

                    if (size > HardOutgoingReceiptsMessageSizeLimit)
                    {
                        throw new SubprotocolException($"Received eth/70 receipts response above hard limit ({size} bytes > {HardOutgoingReceiptsMessageSizeLimit} bytes)");
                    }

                    if (response.TxReceipts.Count > blockHashes.Count - blockIndex)
                    {
                        throw new SubprotocolException("Received more receipts than requested in eth/70 response");
                    }

                    if (response.LastBlockIncomplete && size < SoftOutgoingMessageSizeLimit / 4)
                    {
                        throw new SubprotocolException($"Received partial receipts response below minimum size ({size} bytes < {SoftOutgoingMessageSizeLimit / 4} bytes)");
                    }

                    IOwnedReadOnlyList<TxReceipt[]?> txReceipts = response.TxReceipts;
                    NetworkBlockReceipts? network = txReceipts as NetworkBlockReceipts;

                    for (int i = 0; i < txReceipts.Count; i++)
                    {
                        bool isFirst = i == 0;
                        bool isLast = i == txReceipts.Count - 1;
                        BlockReceiptValidationContext context = validationContexts[blockIndex];

                        // A whole block's receipts kept encoded are checked and kept as received; a block split across
                        // responses is joined decoded.
                        if (network is not null && !(isFirst && firstBlockReceiptIndex > 0) && !(response.LastBlockIncomplete && isLast))
                        {
                            ReadOnlySpan<byte> encoded = network.GetEncoded(i).Span;
                            using (ArrayPoolList<ReceiptFacts> facts = ReadReceiptFacts(encoded, context.ReceiptBehaviors))
                            {
                                ValidateBlockReceipts(new EncodedReceiptFacts(facts), in context, firstBlockReceiptIndex,
                                    isCompleteSegment: true, partialReceiptsGas, partialReceiptsLogsGas, partialReceiptsContentSize);
                            }

                            aggregated.AddEncoded(network, i);
                            blockIndex++;
                            firstBlockReceiptIndex = 0;
                            partialReceiptsGas = 0;
                            partialReceiptsLogsGas = 0;
                            partialReceiptsContentSize = 0;
                            continue;
                        }

                        TxReceipt[] blockReceipts = txReceipts[i] ?? throw new SubprotocolException("Unexpected null receipt block payload");

                        if (isFirst && firstBlockReceiptIndex > 0)
                        {
                            if (partialReceipts is null)
                            {
                                throw new SubprotocolException("Unexpected receipts continuation without pending state");
                            }

                            partialReceipts.AddRange(blockReceipts);
                            ReceiptsValidationResult validationResult = ValidateBlockReceipts(blockReceipts, in context, firstBlockReceiptIndex,
                                !response.LastBlockIncomplete || !isLast, partialReceiptsGas, partialReceiptsLogsGas, partialReceiptsContentSize);
                            partialReceiptsGas = validationResult.GasUsedTotal;
                            partialReceiptsLogsGas = validationResult.LogsGas;
                            partialReceiptsContentSize = validationResult.ReceiptsContentSize;

                            if (response.LastBlockIncomplete && isLast)
                            {
                                if (blockReceipts.Length == 0)
                                {
                                    throw new SubprotocolException("Peer returned no progress for partial receipts");
                                }

                                firstBlockReceiptIndex = partialReceipts.Count;
                            }
                            else
                            {
                                TxReceipt[] completed = partialReceipts.AsSpan().ToArray();
                                aggregated.AddDecoded(completed);
                                partialReceipts.Dispose();
                                partialReceipts = null;
                                blockIndex++;
                                firstBlockReceiptIndex = 0;
                                partialReceiptsGas = 0;
                                partialReceiptsLogsGas = 0;
                                partialReceiptsContentSize = 0;
                            }

                            continue;
                        }

                        if (response.LastBlockIncomplete && isLast)
                        {
                            if (blockReceipts.Length == 0)
                            {
                                throw new SubprotocolException("Peer returned no progress for partial receipts");
                            }

                            // Without a local header nothing bounds EIP-7975 paging of this block,
                            // so stop at the complete prefix; callers treat the short tail as pending.
                            if (context.BlockGasLimit is null)
                            {
                                return (aggregated.Build(), (long)totalResponseSize);
                            }

                            ReceiptsValidationResult validationResult = ValidateBlockReceipts(blockReceipts, in context, firstBlockReceiptIndex,
                                isCompleteSegment: false, partialReceiptsGas, partialReceiptsLogsGas, partialReceiptsContentSize);
                            partialReceipts = new ArrayPoolList<TxReceipt>(blockReceipts.Length + firstBlockReceiptIndex);
                            partialReceipts.AddRange(blockReceipts);
                            firstBlockReceiptIndex = partialReceipts.Count;

                            partialReceiptsGas = validationResult.GasUsedTotal;
                            partialReceiptsLogsGas = validationResult.LogsGas;
                            partialReceiptsContentSize = validationResult.ReceiptsContentSize;

                            continue;
                        }

                        ValidateBlockReceipts(blockReceipts, in context, firstBlockReceiptIndex,
                            isCompleteSegment: true, partialReceiptsGas, partialReceiptsLogsGas, partialReceiptsContentSize);
                        aggregated.AddDecoded(blockReceipts);
                        blockIndex++;
                        firstBlockReceiptIndex = 0;
                        partialReceiptsGas = 0;
                        partialReceiptsLogsGas = 0;
                        partialReceiptsContentSize = 0;
                    }

                    if (!response.LastBlockIncomplete && blockIndex < blockHashes.Count)
                    {
                        break;
                    }
                }
            }

            return (aggregated.Build(), (long)totalResponseSize);
        }
        finally
        {
            partialReceipts?.Dispose();
        }
    }

    /// <summary>A block's receipts as joined from the responses: decoded, or kept encoded as received.</summary>
    /// <param name="Encoded">Where the encoded receipts are in the buffer of encoded blocks, when not decoded.</param>
    private readonly record struct AggregatedReceipts(TxReceipt[]? Decoded, Range Encoded);

    /// <summary>What the receipt checks read from one receipt.</summary>
    /// <param name="Size">The receipt's length as <see cref="ReceiptMessageDecoder"/> encodes it.</param>
    private readonly record struct ReceiptFacts(ulong Size, ulong GasUsedTotal, ulong LogsGas);

    private interface IReceiptFactsSource
    {
        int Count { get; }

        ReceiptFacts Get(int index);
    }

    private readonly struct DecodedReceiptFacts(TxReceipt[] receipts, RlpBehaviors behaviors) : IReceiptFactsSource
    {
        public int Count => receipts.Length;

        public ReceiptFacts Get(int index) =>
            new(GetReceiptSize(receipts[index], behaviors), receipts[index].GasUsedTotal, AddReceiptLogsGas(0, receipts[index]));
    }

    private readonly struct EncodedReceiptFacts(ArrayPoolList<ReceiptFacts> facts) : IReceiptFactsSource
    {
        public int Count => facts.Count;

        public ReceiptFacts Get(int index) => facts[index];
    }

    private sealed class ReceiptAggregationBuilder(int capacity) : IDisposable
    {
        private readonly ArrayPoolList<AggregatedReceipts> _blocks = new(capacity);
        private ArrayPoolList<byte>? _encodedBlocks;
        private RefRlpReaderFunc<TxReceipt[]>? _decodeBlock;
        private bool _allEncoded = true;

        public void AddEncoded(NetworkBlockReceipts receipts, int index)
        {
            ReadOnlySpan<byte> encoded = receipts.GetEncoded(index).Span;
            _encodedBlocks ??= new ArrayPoolList<byte>(encoded.Length);
            _decodeBlock ??= receipts.BlockDecoder;
            int start = _encodedBlocks.Count;
            _encodedBlocks.AddRange(encoded);
            _blocks.Add(new(null, new Range(start, _encodedBlocks.Count)));
        }

        public void AddDecoded(TxReceipt[] receipts)
        {
            _blocks.Add(new(receipts, default));
            _allEncoded = false;
        }

        public IOwnedReadOnlyList<TxReceipt[]> Build()
        {
            if (_allEncoded && _blocks.Count > 0)
            {
                Range[] ranges = new Range[_blocks.Count];
                for (int i = 0; i < ranges.Length; i++) ranges[i] = _blocks[i].Encoded;
                NetworkBlockReceipts result = NetworkBlockReceipts.FromBuffer(_encodedBlocks!, ranges, withBloom: false, _decodeBlock!);
                _encodedBlocks = null;
                return result;
            }

            ArrayPoolList<TxReceipt[]> receipts = new(_blocks.Count);
            try
            {
                foreach (AggregatedReceipts block in _blocks.AsSpan())
                {
                    if (block.Decoded is not null)
                    {
                        receipts.Add(block.Decoded);
                        continue;
                    }

                    RlpReader reader = new(_encodedBlocks!.AsSpan()[block.Encoded]);
                    receipts.Add(_decodeBlock!(ref reader));
                    reader.CheckEnd();
                }

                return receipts;
            }
            catch
            {
                receipts.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _blocks.Dispose();
            _encodedBlocks?.Dispose();
            _encodedBlocks = null;
        }
    }

    private static ArrayPoolList<ReceiptFacts> ReadReceiptFacts(ReadOnlySpan<byte> blockReceipts, RlpBehaviors receiptBehaviors)
    {
        bool eip658 = (receiptBehaviors & RlpBehaviors.Eip658Receipts) != 0;
        RlpReader receipts = new RlpReader(blockReceipts).EnterSequence(out _);
        ArrayPoolList<ReceiptFacts> facts = new(receipts.CountItems());
        while (receipts.HasNext)
        {
            if (!NetworkReceipt.TryRead(ref receipts, withBloom: false, out NetworkReceipt receipt))
                throw new SubprotocolException("Unexpected receipt shape in encoded receipts");
            facts.Add(new ReceiptFacts((ulong)receipt.GetMessageLength(eip658), receipt.GasUsedTotal, receipt.LogsGas));
        }

        return facts;
    }

    private static TxReceipt[] CopyReceipts(TxReceipt[] receipts, int startIndex, int count)
    {
        TxReceipt[] copy = new TxReceipt[count];
        Array.Copy(receipts, startIndex, copy, 0, count);
        return copy;
    }

    private ulong GetBlockReceiptsSize(TxReceipt[] receipts, int startIndex)
    {
        ulong receiptsContentSize = 0;
        ulong lastBlockNumber = NoBlockSeen;
        RlpBehaviors behaviors = RlpBehaviors.None;
        for (int i = startIndex; i < receipts.Length; i++)
        {
            receiptsContentSize += GetReceiptSize(receipts[i], ref lastBlockNumber, ref behaviors);
        }

        return GetBlockReceiptsSize(receiptsContentSize);
    }

    private static ulong GetBlockReceiptsSize(ulong receiptsContentSize) =>
        GetRlpSequenceSize(receiptsContentSize);

    private static ulong GetEth70ReceiptsResponseSize(ulong receiptsContentSize, long requestId)
    {
        ulong receiptsMessageSize = GetRlpSequenceSize(receiptsContentSize);
        ulong responseContentSize =
            (ulong)Rlp.LengthOf(requestId) +
            (ulong)Rlp.LengthOf(1) +
            receiptsMessageSize;

        return GetRlpSequenceSize(responseContentSize);
    }

    private ulong GetReceiptSize(TxReceipt receipt, ref ulong lastBlockNumber, ref RlpBehaviors behaviors)
    {
        if (receipt.BlockNumber != lastBlockNumber)
        {
            lastBlockNumber = receipt.BlockNumber;
            IReceiptSpec receiptSpec = _specProvider.GetReceiptSpec(lastBlockNumber);
            behaviors = receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
        }

        return (ulong)ReceiptMessageDecoder.GetLength(receipt, behaviors);
    }

    private static ulong GetRlpSequenceSize(ulong contentSize) =>
        contentSize > int.MaxValue
            ? ulong.MaxValue
            : (ulong)Rlp.LengthOfSequence((int)contentSize);

    private static GetReceiptsMessage70 BuildRequest(IOwnedReadOnlyList<Hash256> blockHashes, int startIndex, int firstReceiptIndex, ReadOnlyMemory<int> maxReceiptsPerBlock)
    {
        IOwnedReadOnlyList<Hash256> remainingHashes = blockHashes.Slice(startIndex, blockHashes.Count - startIndex);

        return new GetReceiptsMessage70(remainingHashes, firstReceiptIndex) { MaxReceiptsPerBlock = maxReceiptsPerBlock[startIndex..] };
    }

    private async Task<(ReceiptsMessage70 response, ulong size)> SendRequest(GetReceiptsMessage70 message, CancellationToken token)
    {
        Request<GetReceiptsMessage70, ReceiptsMessage70> request = new(message);
        _receiptsRequests70.Send(request);

        ReceiptsMessage70 response = await HandleResponse(request, TransferSpeedType.Receipts, static _ => nameof(GetReceiptsMessage70), token);
        return (response, (ulong)request.ResponseSize);
    }

    private readonly struct ReceiptsResponse(IOwnedReadOnlyList<TxReceipt[]> txReceipts, bool lastBlockIncomplete)
    {
        public IOwnedReadOnlyList<TxReceipt[]> TxReceipts { get; } = txReceipts;

        public bool LastBlockIncomplete { get; } = lastBlockIncomplete;
    }

    private readonly struct ReceiptsValidationResult(ulong gasUsedTotal, ulong logsGas, ulong receiptsContentSize)
    {
        public ulong GasUsedTotal { get; } = gasUsedTotal;

        public ulong LogsGas { get; } = logsGas;

        public ulong ReceiptsContentSize { get; } = receiptsContentSize;
    }

    private readonly record struct BlockReceiptValidationContext(
        ulong ExpectedGasUsed,
        ulong? BlockGasLimit,
        ulong[]? TransactionGasLimits,
        RlpBehaviors ReceiptBehaviors,
        bool ValidateGasUpperBound,
        bool ValidateGasEqual,
        bool IsEip8116Enabled);

    private static ReceiptsValidationResult ValidateBlockReceipts(
        TxReceipt[] blockReceipts,
        in BlockReceiptValidationContext context,
        int firstReceiptIndex,
        bool isCompleteSegment,
        ulong previousGasUsed,
        ulong previousLogsGas,
        ulong previousReceiptsContentSize) =>
        ValidateBlockReceipts(new DecodedReceiptFacts(blockReceipts, context.ReceiptBehaviors), in context,
            firstReceiptIndex, isCompleteSegment, previousGasUsed, previousLogsGas, previousReceiptsContentSize);

    private static ReceiptsValidationResult ValidateBlockReceipts<TReceipts>(
        TReceipts blockReceipts,
        in BlockReceiptValidationContext context,
        int firstReceiptIndex,
        bool isCompleteSegment,
        ulong previousGasUsed,
        ulong previousLogsGas,
        ulong previousReceiptsContentSize) where TReceipts : struct, IReceiptFactsSource
    {
        if (blockReceipts.Count == 0)
        {
            ValidateEmptyReceiptsSegment(firstReceiptIndex, isCompleteSegment);
            ValidateReceiptCount(firstReceiptIndex, blockReceipts.Count, context.TransactionGasLimits, isCompleteSegment);
            ValidateTotalReceiptsSizeAgainstBlockGasLimit(previousReceiptsContentSize, context.BlockGasLimit);
            ValidateSegmentGasAgainstHeader(previousGasUsed, previousLogsGas, context.ExpectedGasUsed, isCompleteSegment,
                context.ValidateGasUpperBound, context.ValidateGasEqual);
            return new ReceiptsValidationResult(previousGasUsed, previousLogsGas, previousReceiptsContentSize);
        }

        previousGasUsed = GetPreviousGasUsedForSegment(firstReceiptIndex, previousGasUsed);
        ulong logsGas = previousLogsGas;
        ulong receiptsContentSize = previousReceiptsContentSize;

        for (int i = 0; i < blockReceipts.Count; i++)
        {
            int receiptIndex = checked(firstReceiptIndex + i);
            ReceiptFacts receipt = blockReceipts.Get(i);
            ValidateReceiptSizeAgainstTransactionGasLimit(receipt.Size, context.TransactionGasLimits, receiptIndex);
            receiptsContentSize = checked(receiptsContentSize + receipt.Size);
            ValidateTotalReceiptsSizeAgainstBlockGasLimit(receiptsContentSize, context.BlockGasLimit);
            previousGasUsed = ValidateReceiptGas(receipt.GasUsedTotal, previousGasUsed, context.IsEip8116Enabled);
            logsGas = checked(logsGas + receipt.LogsGas);
        }

        ValidateReceiptCount(firstReceiptIndex, blockReceipts.Count, context.TransactionGasLimits, isCompleteSegment);
        ValidateSegmentGasAgainstHeader(previousGasUsed, logsGas, context.ExpectedGasUsed, isCompleteSegment,
            context.ValidateGasUpperBound, context.ValidateGasEqual);

        return new ReceiptsValidationResult(previousGasUsed, logsGas, receiptsContentSize);
    }

    private static void ValidateEmptyReceiptsSegment(int firstReceiptIndex, bool isCompleteSegment)
    {
        if (!isCompleteSegment)
        {
            throw new SubprotocolException("Unexpected empty receipt block payload");
        }
    }

    private static ulong GetPreviousGasUsedForSegment(int firstReceiptIndex, ulong previousGasUsed)
    {
        ulong minimalPreviousGasUsed = checked((ulong)firstReceiptIndex * MinimumSupportedTransactionGas);
        return Math.Max(previousGasUsed, minimalPreviousGasUsed);
    }

    private static ulong GetReceiptSize(TxReceipt receipt, RlpBehaviors behaviors) =>
        (ulong)ReceiptMessageDecoder.GetLength(receipt, behaviors);

    /// <returns>The block's gas used up to and including the receipt.</returns>
    /// <remarks>An EIP-8116 receipt holds its transaction's own gas used, so the running total is summed here.</remarks>
    private static ulong ValidateReceiptGas(ulong receiptGasUsedTotal, ulong previousGasUsed, bool isEip8116Enabled)
    {
        ulong receiptGasUsed = isEip8116Enabled ? receiptGasUsedTotal : GetReceiptGasUsed(receiptGasUsedTotal, previousGasUsed);
        ValidateReceiptGasCoversIntrinsicCost(receiptGasUsed);
        return isEip8116Enabled ? checked(previousGasUsed + receiptGasUsed) : receiptGasUsedTotal;
    }

    private static ulong GetReceiptGasUsed(ulong receiptGasUsedTotal, ulong previousGasUsed)
    {
        if (receiptGasUsedTotal < previousGasUsed)
        {
            throw new SubprotocolException("Cumulative gas decreased within block receipts");
        }

        return receiptGasUsedTotal - previousGasUsed;
    }

    private static void ValidateReceiptGasCoversIntrinsicCost(ulong receiptGasUsed)
    {
        if (MinimumSupportedTransactionGas > receiptGasUsed)
        {
            throw new SubprotocolException("Intrinsic gas lower bound exceeds block gas used");
        }
    }

    private static ulong AddReceiptLogsGas(ulong logsGas, TxReceipt receipt)
    {
        if (receipt.Logs is null)
        {
            return logsGas;
        }

        foreach (LogEntry log in receipt.Logs)
        {
            int topics = log.Topics?.Length ?? 0;
            int dataLength = log.Data?.Length ?? 0;
            logsGas = checked(logsGas + GasCostOf.Log + (ulong)topics * GasCostOf.LogTopic + (ulong)dataLength * GasCostOf.LogData);
        }

        return logsGas;
    }

    private static void ValidateReceiptCount(
        int firstReceiptIndex,
        int deliveredReceiptCount,
        ulong[]? transactionGasLimits,
        bool isCompleteSegment)
    {
        if (transactionGasLimits is null)
        {
            return;
        }

        int deliveredReceiptsEnd = checked(firstReceiptIndex + deliveredReceiptCount);
        if (deliveredReceiptsEnd > transactionGasLimits.Length)
        {
            throw new SubprotocolException("Receipt count exceeds block transactions count");
        }

        if (isCompleteSegment && deliveredReceiptsEnd != transactionGasLimits.Length)
        {
            throw new SubprotocolException("Receipt count mismatch with block transactions count");
        }
    }

    private static void ValidateReceiptSizeAgainstTransactionGasLimit(
        ulong receiptSize,
        ulong[]? transactionGasLimits,
        int receiptIndex)
    {
        if (transactionGasLimits is null)
        {
            return;
        }

        if (receiptIndex >= transactionGasLimits.Length)
        {
            throw new SubprotocolException("Receipt count exceeds block transactions count");
        }

        ulong maxReceiptSize = GetReceiptSizeLimit(transactionGasLimits[receiptIndex]);
        if (receiptSize > maxReceiptSize)
        {
            throw new SubprotocolException("Receipt size exceeds transaction gas limit allowance");
        }
    }

    private static void ValidateTotalReceiptsSizeAgainstBlockGasLimit(ulong receiptsContentSize, ulong? blockGasLimit)
    {
        // No local header means no allowance to check; an empty list fits any allowance, even a zero gas limit's.
        if (blockGasLimit is not { } gasLimit || receiptsContentSize == 0)
        {
            return;
        }

        ulong blockReceiptsSize = GetBlockReceiptsSize(receiptsContentSize);
        ulong maxReceiptsSize = GetReceiptSizeLimit(gasLimit);
        if (blockReceiptsSize > maxReceiptsSize)
        {
            throw new SubprotocolException("Block receipts size exceeds block gas limit allowance");
        }
    }

    private static ulong GetReceiptSizeLimit(ulong gasLimit) => gasLimit / 8;

    private static void ValidateSegmentGasAgainstHeader(
        ulong actualGasUsed,
        ulong logsGas,
        ulong expectedGasUsed,
        bool isCompleteSegment,
        bool validateReceiptGasUpperBoundAgainstHeader,
        bool validateReceiptGasEqualToHeader)
    {
        if (validateReceiptGasUpperBoundAgainstHeader && expectedGasUsed > 0 && actualGasUsed > expectedGasUsed)
        {
            throw new SubprotocolException("Block gas used exceeds header value");
        }

        if (isCompleteSegment)
        {
            ulong gasUpperBound = validateReceiptGasUpperBoundAgainstHeader && expectedGasUsed > 0 ? expectedGasUsed : actualGasUsed;
            if (logsGas > gasUpperBound)
            {
                throw new SubprotocolException("Logs gas exceeds block gas used");
            }

            if (validateReceiptGasEqualToHeader && expectedGasUsed > 0 && actualGasUsed != expectedGasUsed)
            {
                throw new SubprotocolException("Block gas used mismatch between receipts and header");
            }
        }
    }

}
