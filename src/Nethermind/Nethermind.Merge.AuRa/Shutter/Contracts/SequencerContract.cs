// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.Int256;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

using TransactionSubmitted = ISequencerContract.TransactionSubmitted;

public class SequencerContract : Contract
{
    private readonly ILogFinder _logFinder;
    private readonly IFilterStore _filterStore;
    private readonly AbiEncodingInfo _transactionSubmittedAbi;
    private readonly long LogScanChunkSize = 16;
    private readonly int LogScanCutoffChunks = 4;

    public SequencerContract(string address, ILogFinder logFinder, IFilterStore filterStore)
        : base(null, new(address), null)
    {
        _transactionSubmittedAbi = AbiDefinition.GetEvent(nameof(TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
        _logFinder = logFinder;
        _filterStore = filterStore;
    }

    public IEnumerable<TransactionSubmitted> GetEvents(ulong eon, ulong txPointer)
    {
        IEnumerable<TransactionSubmitted> events = [];

        IEnumerable<object> topics = new List<object>() { _transactionSubmittedAbi.Signature.Hash };
        LogFilter logFilter;

        BlockParameter end = BlockParameter.Latest;
        BlockParameter start;

        for (int i = 0; i < LogScanCutoffChunks; i++)
        {
            start = new(end.BlockNumber!.Value - LogScanChunkSize);
            logFilter = _filterStore.CreateLogFilter(start, end, ContractAddress!, topics);;

            IEnumerable<FilterLog> logs = _logFinder.FindLogs(logFilter);
            List<TransactionSubmitted> newEvents = logs
                .AsParallel()
                .Select(ParseTransactionSubmitted)
                .Where(e => e.Eon == eon && e.TxIndex >= txPointer)
                .ToList();
            events = newEvents.Concat(events);

            if (!newEvents.IsNullOrEmpty())
            {
                TransactionSubmitted tx0 = newEvents.ElementAt(0);
                if (tx0.Eon < eon || tx0.TxIndex <= txPointer)
                {
                    break;
                }
            }

            end = start;
        }

        return events;
    }

    private TransactionSubmitted ParseTransactionSubmitted(FilterLog log)
    {
        object[] decodedEvent = AbiEncoder.Decode(AbiEncodingStyle.None, _transactionSubmittedAbi.Signature, log.Data);
        return new TransactionSubmitted()
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
