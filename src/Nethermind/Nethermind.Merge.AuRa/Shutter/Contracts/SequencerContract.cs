// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.Int256;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class SequencerContract : Contract
{
    private readonly ILogFinder _logFinder;
    private readonly LogFilter _logFilter;
    private readonly AbiEncodingInfo _transactionSubmittedAbi;

    public SequencerContract(string contractAddress, ILogFinder logFinder, IFilterStore filterStore)
        : base(null, new(contractAddress), null)
    {
        _transactionSubmittedAbi = AbiDefinition.GetEvent(nameof(ISequencerContract.TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
        IEnumerable<object> topics = new List<object>() { _transactionSubmittedAbi.Signature.Hash };
        _logFinder = logFinder;
        _logFilter = filterStore.CreateLogFilter(BlockParameter.Earliest, BlockParameter.Latest, contractAddress, topics);
    }

    public IEnumerable<ISequencerContract.TransactionSubmitted> GetEvents()
    {
        IEnumerable<FilterLog> logs = _logFinder!.FindLogs(_logFilter!);
        return logs.Select(log => ParseTransactionSubmitted(AbiEncoder.Decode(AbiEncodingStyle.None, _transactionSubmittedAbi.Signature, log.Data)));
    }

    public IEnumerable<ISequencerContract.TransactionSubmitted> GetEvents(ulong eon)
    {
        return GetEvents().Where(e => e.Eon == eon);
    }

    private ISequencerContract.TransactionSubmitted ParseTransactionSubmitted(object[] decodedEvent)
    {
        return new ISequencerContract.TransactionSubmitted()
        {
            Eon = (ulong)decodedEvent[0],
            IdentityPrefix = new Bytes32((byte[])decodedEvent[1]),
            Sender = (Address)decodedEvent[2],
            EncryptedTransaction = (byte[])decodedEvent[3],
            GasLimit = (UInt256)decodedEvent[4]
        };
    }
}
