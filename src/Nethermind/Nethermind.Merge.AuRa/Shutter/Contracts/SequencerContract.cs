// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class SequencerContract : Contract
{
    private readonly ILogFinder _logFinder;
    private readonly AbiEncodingInfo _transactionSubmittedAbi;
    private const long LogScanChunkSize = 16;
    private const int LogScanCutoffChunks = 128;
    private readonly AddressFilter _addressFilter;
    private readonly TopicsFilter _topicsFilter;
    private readonly ILogger _logger;

    public SequencerContract(Address address, ILogFinder logFinder, ILogManager logManager)
        : base(null, address)
    {
        _transactionSubmittedAbi = AbiDefinition.GetEvent(nameof(ISequencerContract.TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
        _addressFilter = new AddressFilter(ContractAddress!);
        _topicsFilter = new SequenceTopicsFilter(new SpecificTopic(_transactionSubmittedAbi.Signature.Hash));
        _logFinder = logFinder;
        _logger = logManager.GetClassLogger();
    }

    public IEnumerable<ISequencerContract.TransactionSubmitted> GetEvents(ulong eon, ulong txPointer, long headBlockNumber)
    {
        List<List<ISequencerContract.TransactionSubmitted>> eventBlocks = GetEventBlocks(eon, txPointer, headBlockNumber, out int len);
        for (int i = len - 1; i >= 0; i--)
        {
            foreach (ISequencerContract.TransactionSubmitted tx in eventBlocks[i])
            {
                yield return tx;
            }
        }
    }

    public bool FilterAccepts(LogEntry log, long blockNumber)
        => new LogFilter(0, new(blockNumber), new(blockNumber), _addressFilter, _topicsFilter).Accepts(log);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ISequencerContract.TransactionSubmitted ParseTransactionSubmitted(LogEntry log)
    {
        object[] decodedEvent = AbiEncoder.Decode(AbiEncodingStyle.None, _transactionSubmittedAbi.Signature, log.Data);
        return new ISequencerContract.TransactionSubmitted
        {
            Eon = (ulong)decodedEvent[0],
            TxIndex = (ulong)decodedEvent[1],
            IdentityPrefix = new Bytes32((byte[])decodedEvent[2]),
            Sender = (Address)decodedEvent[3],
            EncryptedTransaction = (byte[])decodedEvent[4],
            GasLimit = (UInt256)decodedEvent[5]
        };
    }

    private List<List<ISequencerContract.TransactionSubmitted>> GetEventBlocks(ulong eon, ulong txPointer, long headBlockNumber, out int count)
    {
        List<List<ISequencerContract.TransactionSubmitted>> eventBlocks = [];

        BlockParameter end = new(headBlockNumber);

        count = 0;
        for (int i = 0; i < LogScanCutoffChunks; i++)
        {
            long startBlockNumber = end.BlockNumber!.Value - LogScanChunkSize;
            BlockParameter start = new(startBlockNumber);
            LogFilter logFilter = new(0, start, end, _addressFilter, _topicsFilter);

            List<FilterLog> logs;
            try
            {
                logs = _logFinder.FindLogs(logFilter).ToList();
            }
            catch (ResourceNotFoundException e)
            {
                if (_logger.IsDebug) _logger.Warn($"Cannot fetch Shutter transactions from logs: {e}");
                break;
            }

            List<ISequencerContract.TransactionSubmitted> events = eventsFromLogs(logs, eon, txPointer, start.BlockNumber!.Value, end.BlockNumber!.Value, out int len);
            eventBlocks.Add(events);
            count++;

            if (len > 0)
            {
                ISequencerContract.TransactionSubmitted tx = events.First();
                if (tx.Eon < eon || tx.TxIndex <= txPointer)
                {
                    break;
                }
            }

            end = new BlockParameter(startBlockNumber - 1);
        }

        return eventBlocks;
    }

    private List<ISequencerContract.TransactionSubmitted> eventsFromLogs(List<FilterLog> logs, ulong eon, ulong txPointer, long startBlock, long endBlock, out int eventCount)
    {
        List<ISequencerContract.TransactionSubmitted> events = [];

        eventCount = 0;
        int logCount = 0;
        foreach (FilterLog log in logs)
        {
            ISequencerContract.TransactionSubmitted tx = ParseTransactionSubmitted(log);
            if (tx.Eon == eon && tx.TxIndex >= txPointer)
            {
                events.Add(tx);
                eventCount++;
            }
            logCount++;
        }

        if (_logger.IsDebug) _logger.Debug($"Found {eventCount} Shutter events from {logCount} logs in block range {startBlock} - {endBlock}");

        return events;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ISequencerContract.TransactionSubmitted ParseTransactionSubmitted(FilterLog log)
    {
        object[] decodedEvent = AbiEncoder.Decode(AbiEncodingStyle.None, _transactionSubmittedAbi.Signature, log.Data);
        return new ISequencerContract.TransactionSubmitted
        {
            Eon = (ulong)decodedEvent[0],
            TxIndex = (ulong)decodedEvent[1],
            IdentityPrefix = new Bytes32((byte[])decodedEvent[2]),
            Sender = (Address)decodedEvent[3],
            EncryptedTransaction = (byte[])decodedEvent[4],
            GasLimit = (UInt256)decodedEvent[5]
        };
    }
}
