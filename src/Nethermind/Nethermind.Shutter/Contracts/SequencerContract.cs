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

namespace Nethermind.Shutter.Contracts;

public class SequencerContract : Contract
{
    public readonly AbiEncodingInfo TransactionSubmittedAbi;
    private readonly ILogFinder _logFinder;
    private const long LogScanChunkSize = 16;
    private const int LogScanCutoffChunks = 128;
    private readonly AddressFilter _addressFilter;
    private readonly TopicsFilter _topicsFilter;
    private readonly ILogger _logger;

    public SequencerContract(Address address, ILogFinder logFinder, ILogManager logManager)
        : base(null, address)
    {
        TransactionSubmittedAbi = AbiDefinition.GetEvent(nameof(ISequencerContract.TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
        _addressFilter = new AddressFilter(ContractAddress!);
        _topicsFilter = new SequenceTopicsFilter(new SpecificTopic(TransactionSubmittedAbi.Signature.Hash));
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

    public LogFilter CreateFilter(long blockNumber)
        => new(0, new(blockNumber), new(blockNumber), _addressFilter, _topicsFilter);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ISequencerContract.TransactionSubmitted ParseTransactionSubmitted(ILogEntry log)
    {
        object[] decodedEvent = AbiEncoder.Decode(AbiEncodingStyle.None, TransactionSubmittedAbi.Signature, log.Data);
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
            bool atGenesis = false;
            long startBlockNumber = end.BlockNumber!.Value - LogScanChunkSize;
            if (startBlockNumber < 0)
            {
                atGenesis = true;
                startBlockNumber = 0;
            }

            BlockParameter start = new(startBlockNumber);
            LogFilter logFilter = new(0, start, end, _addressFilter, _topicsFilter);

            List<FilterLog> logs;
            try
            {
                logs = _logFinder.FindLogs(logFilter).ToList();
            }
            catch (ResourceNotFoundException e)
            {
                _logger.Warn($"Cannot fetch Shutter events from logs: {e}");
                break;
            }

            List<ISequencerContract.TransactionSubmitted> events = EventsFromLogs(logs, eon, txPointer, start.BlockNumber!.Value, end.BlockNumber!.Value, out int len);
            eventBlocks.Add(events);
            count++;

            if (atGenesis)
            {
                break;
            }

            if (len > 0)
            {
                ISequencerContract.TransactionSubmitted tx = events.First();
                // todo: fix this, broken by submitting transaction with lower eon after higher eon
                if (tx.Eon < eon || tx.TxIndex <= txPointer)
                {
                    break;
                }
            }

            end = new BlockParameter(startBlockNumber - 1);
        }

        return eventBlocks;
    }

    private List<ISequencerContract.TransactionSubmitted> EventsFromLogs(List<FilterLog> logs, ulong eon, ulong txPointer, long startBlock, long endBlock, out int eventCount)
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

        _logger.Debug($"Found {eventCount} Shutter events from {logCount} logs in block range {startBlock} - {endBlock}");

        return events;
    }
}
