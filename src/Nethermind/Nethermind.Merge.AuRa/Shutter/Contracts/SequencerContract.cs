// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
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
    private const long LogScanChunkSize = 16;
    private const int LogScanCutoffChunks = 16;
    private readonly AddressFilter _addressFilter;
    private readonly TopicsFilter _topicsFilter;

    public SequencerContract(string address, ILogFinder logFinder, IFilterStore filterStore)
        : base(null, new(address), null)
    {
        _transactionSubmittedAbi = AbiDefinition.GetEvent(nameof(TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
        _addressFilter = new AddressFilter(ContractAddress!);
        _topicsFilter = new SequenceTopicsFilter(new SpecificTopic(_transactionSubmittedAbi.Signature.Hash));
        _logFinder = logFinder;
        _filterStore = filterStore;
    }

    public IEnumerable<TransactionSubmitted> GetEvents(ulong eon, ulong txPointer, long headBlockNumber)
    {
        IEnumerable<TransactionSubmitted> events = [];
        BlockParameter end = new(headBlockNumber);

        for (int i = 0; i < LogScanCutoffChunks; i++)
        {
            long startBlockNumber = end.BlockNumber!.Value - LogScanChunkSize;
            BlockParameter start = new(startBlockNumber);
            LogFilter logFilter = new(0, start, end, _addressFilter, _topicsFilter);

            IEnumerable<TransactionSubmitted> transactions = _logFinder
                .FindLogs(logFilter)
                .AsParallel()
                .AsOrdered()
                .Select(ParseTransactionSubmitted);

            foreach (TransactionSubmitted tx in transactions)
            {
                // if transaction in chunk is before txPointer then don't search further
                if (tx.Eon < eon || tx.TxIndex <= txPointer)
                {
                    yield break;
                }

                if (tx.Eon == eon && tx.TxIndex >= txPointer)
                {
                    yield return tx;
                }
            }

            end = new BlockParameter(startBlockNumber - 1);
        }
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
