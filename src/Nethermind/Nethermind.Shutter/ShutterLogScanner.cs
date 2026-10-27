// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Runtime.CompilerServices;
using Nethermind.Abi;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Core;
using Nethermind.Facade.Find;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Shutter.Contracts;

namespace Nethermind.Shutter;

public class ShutterLogScanner(
    SequencerContract sequencerContract,
    ILogFinder logFinder,
    ILogManager logManager)
        : LogScanner<ISequencerContract.TransactionSubmitted>(
            logFinder,
            new AddressFilter(sequencerContract.ContractAddress!),
            new SequenceTopicsFilter(new SpecificTopic(SequencerContract.TransactionSubmittedHash)),
            logManager)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override ISequencerContract.TransactionSubmitted ParseEvent(ILogEntry log)
    {
        (ulong eon, ulong txIndex, byte[] identityPrefix, Address sender, byte[] encryptedTransaction, UInt256 gasLimit) = AbiCodec.Decode(SequencerContract.TransactionSubmittedData, log.Data);
        return new ISequencerContract.TransactionSubmitted
        {
            Eon = eon,
            TxIndex = txIndex,
            IdentityPrefix = new Bytes32(identityPrefix),
            Sender = sender,
            EncryptedTransaction = encryptedTransaction,
            GasLimit = gasLimit
        };
    }
}
