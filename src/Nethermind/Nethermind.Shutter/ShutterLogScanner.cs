// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Runtime.CompilerServices;
using Nethermind.Abi;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Facade.Find;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Shutter.Contracts;

namespace Nethermind.Shutter;

public class ShutterLogScanner(
    SequencerContract sequencerContract,
    ILogFinder logFinder,
    ILogManager logManager,
    IAbiEncoder abiEncoder)
        : LogScanner<ISequencerContract.TransactionSubmitted>(
            logFinder,
            new AddressFilter(sequencerContract.ContractAddress!),
            new SequenceTopicsFilter(new SpecificTopic(sequencerContract.TransactionSubmittedAbi.Signature.Hash)),
            logManager)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override ISequencerContract.TransactionSubmitted ParseEvent(ILogEntry log)
    {
        object[] decodedEvent = abiEncoder.Decode(AbiEncodingStyle.None, sequencerContract.TransactionSubmittedAbi.Signature, log.Data);
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
