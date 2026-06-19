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
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.Rlpx;
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
        ISpecProvider specProvider,
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
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth70MessageCode.Receipts:
                ReceiptsMessage70 receiptsMessage = Deserialize<ReceiptsMessage70>(message.Content);
                ReportIn(receiptsMessage, size);
                Handle(receiptsMessage, size);
                return true;
            case Eth70MessageCode.GetReceipts:
                HandleInBackground<GetReceiptsMessage70, ReceiptsMessage70>(message, Handle);
                return true;
            default:
                return base.HandleMessageCore(message);
        }
    }

    private void Handle(ReceiptsMessage70 msg, long size) => _receiptsRequests70.Handle(msg.RequestId, msg, size);

    internal ValueTask<ReceiptsMessage70> Handle(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        using GetReceiptsMessage70 message = getReceiptsMessage;
        ReceiptsResponse response = FulfillReceiptsRequest(message, cancellationToken);
        return ValueTask.FromResult(new ReceiptsMessage70(message.RequestId, response.TxReceipts, response.LastBlockIncomplete));
    }

    private ReceiptsResponse FulfillReceiptsRequest(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        ReadOnlySpan<Hash256> hashes = getReceiptsMessage.Hashes.AsSpan();
        ArrayPoolList<TxReceipt[]> txReceipts = new(hashes.Length);
        bool lastBlockIncomplete = false;

        try
        {
            ulong responseReceiptsContentSize = 0;
            for (int blockIndex = 0; blockIndex < hashes.Length; blockIndex++)
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

                if (startIndex >= receipts.Length)
                {
                    throw new SubprotocolException($"Invalid firstBlockReceiptIndex {startIndex} for block receipts length {receipts.Length}");
                }

                ulong remainingBlockSize = GetBlockReceiptsSize(receipts, startIndex);
                if (GetEth70ReceiptsResponseSize(
                    responseReceiptsContentSize + remainingBlockSize,
                    getReceiptsMessage.RequestId) <= SoftOutgoingMessageSizeLimit)
                {
                    txReceipts.Add(CopyReceipts(receipts, startIndex, receipts.Length - startIndex));
                    responseReceiptsContentSize += remainingBlockSize;
                    continue;
                }

                if (responseReceiptsContentSize > 0)
                {
                    break;
                }

                if (GetEth70ReceiptsResponseSize(remainingBlockSize, getReceiptsMessage.RequestId) <= HardOutgoingReceiptsMessageSizeLimit)
                {
                    txReceipts.Add(CopyReceipts(receipts, startIndex, receipts.Length - startIndex));
                    break;
                }

                ulong blockReceiptsContentSize = 0;
                int taken = 0;
                ulong lastBlockNumber = ulong.MaxValue; // Sentinel: no block seen yet
                RlpBehaviors behaviors = RlpBehaviors.None;
                for (int receiptIndex = startIndex; receiptIndex < receipts.Length; receiptIndex++)
                {
                    ulong receiptSize = GetReceiptSize(receipts[receiptIndex], ref lastBlockNumber, ref behaviors);
                    ulong nextBlockReceiptsContentSize = blockReceiptsContentSize + receiptSize;
                    ulong nextBlockSize = GetBlockReceiptsSize(nextBlockReceiptsContentSize);
                    if (GetEth70ReceiptsResponseSize(nextBlockSize, getReceiptsMessage.RequestId) > HardOutgoingReceiptsMessageSizeLimit)
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

    public override async Task<IOwnedReadOnlyList<TxReceipt[]>> GetReceipts(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
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
                return await SendGetReceiptsWithPaging(ownedHashes, token);
            });
    }

    private async Task<(IOwnedReadOnlyList<TxReceipt[]>, long)> SendGetReceiptsWithPaging(IOwnedReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        ArrayPoolList<TxReceipt[]> aggregated = new(blockHashes.Count);
        ArrayPoolList<TxReceipt>? partialReceipts = null;
        ulong partialReceiptsGas = 0;
        ulong partialReceiptsLogsGas = 0;
        ulong partialReceiptsContentSize = 0;
        int blockIndex = 0;
        int firstBlockReceiptIndex = 0;
        ulong totalResponseSize = 0;

        using ArrayPoolList<ulong> expectedGasUsed = new(blockHashes.Count);
        using ArrayPoolList<ulong> blockGasLimits = new(blockHashes.Count);
        using ArrayPoolList<Transaction[]?> blockTransactions = new(blockHashes.Count);
        using ArrayPoolList<RlpBehaviors> receiptRlpBehaviors = new(blockHashes.Count);
        using ArrayPoolList<bool> validateReceiptGasUpperBoundAgainstHeader = new(blockHashes.Count);
        using ArrayPoolList<bool> validateReceiptGasEqualToHeader = new(blockHashes.Count);

        {
            ReadOnlySpan<Hash256> blockHashesSpan = blockHashes.AsSpan();
            for (int i = 0; i < blockHashesSpan.Length; i++)
            {
                Hash256 blockHash = blockHashesSpan[i];
                BlockHeader? header = SyncServer.FindHeader(blockHash);
                if (header is null)
                {
                    expectedGasUsed.Add(0);
                    blockGasLimits.Add(0);
                    blockTransactions.Add(null);
                    receiptRlpBehaviors.Add(RlpBehaviors.None);
                    validateReceiptGasUpperBoundAgainstHeader.Add(false);
                    validateReceiptGasEqualToHeader.Add(false);
                    continue;
                }

                IReleaseSpec spec = _specProvider.GetSpec(header);
                Block? block = SyncServer.Find(blockHash);
                expectedGasUsed.Add(header.GasUsed);
                blockGasLimits.Add(header.GasLimit);
                blockTransactions.Add(GetTransactionsForReceiptValidation(block));
                receiptRlpBehaviors.Add(spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
                validateReceiptGasUpperBoundAgainstHeader.Add(!spec.IsEip8037Enabled);
                validateReceiptGasEqualToHeader.Add(!spec.IsEip7778Enabled && !spec.IsEip8037Enabled);
            }
        }

        try
        {
            while (blockIndex < blockHashes.Count)
            {
                using GetReceiptsMessage70 request = BuildRequest(blockHashes, blockIndex, firstBlockReceiptIndex);
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

                    ReadOnlySpan<TxReceipt[]?> txReceipts = response.TxReceipts.AsSpan();

                    for (int i = 0; i < txReceipts.Length; i++)
                    {
                        bool isFirst = i == 0;
                        bool isLast = i == txReceipts.Length - 1;
                        TxReceipt[] blockReceipts = txReceipts[i] ?? throw new SubprotocolException("Unexpected null receipt block payload");
                        ulong blockExpectedGasUsed = expectedGasUsed[blockIndex];
                        ulong blockGasLimit = blockGasLimits[blockIndex];
                        Transaction[]? transactions = blockTransactions[blockIndex];
                        RlpBehaviors receiptBehaviors = receiptRlpBehaviors[blockIndex];
                        bool validateGasUpperBound = validateReceiptGasUpperBoundAgainstHeader[blockIndex];
                        bool validateGasEqual = validateReceiptGasEqualToHeader[blockIndex];

                        if (isFirst && firstBlockReceiptIndex > 0)
                        {
                            if (partialReceipts is null)
                            {
                                throw new SubprotocolException("Unexpected receipts continuation without pending state");
                            }

                            partialReceiptsGas = partialReceipts[^1].GasUsedTotal;
                            partialReceipts.AddRange(blockReceipts);
                            ReceiptsValidationResult validationResult = ValidateBlockReceipts(blockReceipts, blockExpectedGasUsed,
                                blockGasLimit, transactions, receiptBehaviors, validateGasUpperBound, validateGasEqual, firstBlockReceiptIndex,
                                !response.LastBlockIncomplete || !isLast, partialReceiptsGas, partialReceiptsLogsGas,
                                partialReceiptsContentSize);
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
                                aggregated.Add(completed);
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

                            ReceiptsValidationResult validationResult = ValidateBlockReceipts(blockReceipts, blockExpectedGasUsed,
                                blockGasLimit, transactions, receiptBehaviors, validateGasUpperBound, validateGasEqual, firstBlockReceiptIndex,
                                false, partialReceiptsGas, partialReceiptsLogsGas, partialReceiptsContentSize);
                            partialReceipts = new ArrayPoolList<TxReceipt>(blockReceipts.Length + firstBlockReceiptIndex);
                            partialReceipts.AddRange(blockReceipts);
                            firstBlockReceiptIndex = partialReceipts.Count;

                            partialReceiptsGas = validationResult.GasUsedTotal;
                            partialReceiptsLogsGas = validationResult.LogsGas;
                            partialReceiptsContentSize = validationResult.ReceiptsContentSize;

                            continue;
                        }

                        ValidateBlockReceipts(blockReceipts, blockExpectedGasUsed, blockGasLimit, transactions,
                            receiptBehaviors, validateGasUpperBound, validateGasEqual, firstBlockReceiptIndex, true, partialReceiptsGas,
                            partialReceiptsLogsGas, partialReceiptsContentSize);
                        aggregated.Add(blockReceipts);
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

            return (aggregated, (long)totalResponseSize);
        }
        catch
        {
            aggregated.Dispose();
            partialReceipts?.Dispose();
            throw;
        }
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
        ulong lastBlockNumber = ulong.MaxValue; // Sentinel: no block seen yet
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

    private static GetReceiptsMessage70 BuildRequest(IOwnedReadOnlyList<Hash256> blockHashes, int startIndex, int firstReceiptIndex)
    {
        IOwnedReadOnlyList<Hash256> remainingHashes = blockHashes.Slice(startIndex, blockHashes.Count - startIndex);

        return new GetReceiptsMessage70(remainingHashes, firstReceiptIndex);
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

    private ReceiptsValidationResult ValidateBlockReceipts(
        TxReceipt[] blockReceipts,
        ulong expectedGasUsed,
        ulong blockGasLimit,
        Transaction[]? transactions,
        RlpBehaviors receiptBehaviors,
        bool validateReceiptGasUpperBoundAgainstHeader,
        bool validateReceiptGasEqualToHeader,
        int firstReceiptIndex,
        bool isCompleteSegment,
        ulong previousGasUsed,
        ulong previousLogsGas,
        ulong previousReceiptsContentSize)
    {
        if (blockReceipts is { Length: 0 })
        {
            ValidateEmptyReceiptsSegment(firstReceiptIndex, isCompleteSegment);
            ValidateReceiptCount(firstReceiptIndex, blockReceipts.Length, transactions, isCompleteSegment);
            ValidateTotalReceiptsSizeAgainstBlockGasLimit(previousReceiptsContentSize, blockGasLimit);
            return new ReceiptsValidationResult(previousGasUsed, previousLogsGas, previousReceiptsContentSize);
        }

        previousGasUsed = GetPreviousGasUsedForSegment(firstReceiptIndex, previousGasUsed);
        ulong logsGas = previousLogsGas;
        ulong receiptsContentSize = previousReceiptsContentSize;

        for (int i = 0; i < blockReceipts.Length; i++)
        {
            int receiptIndex = checked(firstReceiptIndex + i);
            TxReceipt receipt = blockReceipts[i];
            ulong receiptSize = GetReceiptSize(receipt, receiptBehaviors);
            ValidateReceiptSizeAgainstTransactionGasLimit(receiptSize, transactions, receiptIndex);
            receiptsContentSize = checked(receiptsContentSize + receiptSize);
            ValidateTotalReceiptsSizeAgainstBlockGasLimit(receiptsContentSize, blockGasLimit);
            previousGasUsed = ValidateReceiptGas(receipt, previousGasUsed);
            logsGas = AddReceiptLogsGas(logsGas, receipt);
        }

        ValidateReceiptCount(firstReceiptIndex, blockReceipts.Length, transactions, isCompleteSegment);
        ValidateSegmentGasAgainstHeader(previousGasUsed, logsGas, expectedGasUsed, isCompleteSegment,
            validateReceiptGasUpperBoundAgainstHeader, validateReceiptGasEqualToHeader);

        return new ReceiptsValidationResult(previousGasUsed, logsGas, receiptsContentSize);
    }

    private static void ValidateEmptyReceiptsSegment(int firstReceiptIndex, bool isCompleteSegment)
    {
        if (firstReceiptIndex != 0 || !isCompleteSegment)
        {
            throw new SubprotocolException("Unexpected empty receipt block payload");
        }
    }

    private static ulong GetPreviousGasUsedForSegment(int firstReceiptIndex, ulong previousGasUsed)
    {
        ulong minimalPreviousGasUsed = checked((ulong)firstReceiptIndex * GasCostOf.Transaction);
        return Math.Max(previousGasUsed, minimalPreviousGasUsed);
    }

    private static Transaction[]? GetTransactionsForReceiptValidation(Block? block) =>
        block is { IsBodyMissing: false } ? block.Transactions : null;

    private static ulong GetReceiptSize(TxReceipt receipt, RlpBehaviors behaviors) =>
        (ulong)ReceiptMessageDecoder.GetLength(receipt, behaviors);

    private static ulong ValidateReceiptGas(TxReceipt receipt, ulong previousGasUsed)
    {
        ulong receiptGasUsed = GetReceiptGasUsed(receipt, previousGasUsed);
        ValidateReceiptGasCoversIntrinsicCost(receiptGasUsed);
        return receipt.GasUsedTotal;
    }

    private static ulong GetReceiptGasUsed(TxReceipt receipt, ulong previousGasUsed)
    {
        if (receipt.GasUsedTotal < previousGasUsed)
        {
            throw new SubprotocolException("Cumulative gas decreased within block receipts");
        }

        return receipt.GasUsedTotal - previousGasUsed;
    }

    private static void ValidateReceiptGasCoversIntrinsicCost(ulong receiptGasUsed)
    {
        if (GasCostOf.Transaction > receiptGasUsed)
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
        Transaction[]? transactions,
        bool isCompleteSegment)
    {
        if (transactions is null)
        {
            return;
        }

        int deliveredReceiptsEnd = checked(firstReceiptIndex + deliveredReceiptCount);
        if (deliveredReceiptsEnd > transactions.Length)
        {
            throw new SubprotocolException("Receipt count exceeds block transactions count");
        }

        if (isCompleteSegment && deliveredReceiptsEnd != transactions.Length)
        {
            throw new SubprotocolException("Receipt count mismatch with block transactions count");
        }
    }

    private static void ValidateReceiptSizeAgainstTransactionGasLimit(
        ulong receiptSize,
        Transaction[]? transactions,
        int receiptIndex)
    {
        if (transactions is null)
        {
            return;
        }

        if (receiptIndex >= transactions.Length)
        {
            throw new SubprotocolException("Receipt count exceeds block transactions count");
        }

        ulong maxReceiptSize = GetReceiptSizeLimit(transactions[receiptIndex].GasLimit);
        if (receiptSize > maxReceiptSize)
        {
            throw new SubprotocolException("Receipt size exceeds transaction gas limit allowance");
        }
    }

    private static void ValidateTotalReceiptsSizeAgainstBlockGasLimit(ulong receiptsContentSize, ulong blockGasLimit)
    {
        if (blockGasLimit == 0)
        {
            return;
        }

        ulong blockReceiptsSize = GetBlockReceiptsSize(receiptsContentSize);
        ulong maxReceiptsSize = GetReceiptSizeLimit(blockGasLimit);
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
