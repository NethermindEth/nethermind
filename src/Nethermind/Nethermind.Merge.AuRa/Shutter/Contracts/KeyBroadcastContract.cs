// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class KeyBroadcastContract : CallableContract, IKeyBroadcastContract
{
    private const string getEonKey = "getEonKey";

    public KeyBroadcastContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
    }

    public byte[] GetEonKey(BlockHeader blockHeader, in ulong eon)
    {
        return (byte[])Call(blockHeader, getEonKey, Address.Zero, [eon])[0];
    }
}
