// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class SequencerContract : Contract
{
    private readonly ILogFinder _logFinder;
    private readonly LogFilter _logFilter;
    private readonly AbiEncodingInfo TransactionSubmittedAbi;

    public SequencerContract(string contractAddress, ILogFinder logFinder, IFilterStore filterStore)
        : base(null, new(contractAddress), null)
    {
        TransactionSubmittedAbi = AbiDefinition.GetEvent(nameof(ISequencerContract.TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
        IEnumerable<object> topics = new List<object>() { TransactionSubmittedAbi.Signature.Hash };
        _logFinder = logFinder;
        _logFilter = filterStore.CreateLogFilter(BlockParameter.Earliest, BlockParameter.Latest, contractAddress, topics);
    }

    public IEnumerable<ISequencerContract.TransactionSubmitted> GetEvents()
    {
        IEnumerable<IFilterLog> logs = _logFinder!.FindLogs(_logFilter!);
        return logs.Select(log => ParseTransactionSubmitted(AbiEncoder.Decode(AbiEncodingStyle.None, TransactionSubmittedAbi.Signature, log.Data)));
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
