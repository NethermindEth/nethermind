// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Shutter.Contracts;

public class KeyBroadcastContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress) : CallableContract(transactionProcessor, abiEncoder, contractAddress), IKeyBroadcastContract
{
    public byte[] GetEonKey(BlockHeader blockHeader, in ulong eon)
    {
        return (byte[])Call(blockHeader, nameof(GetEonKey), Address.Zero, [eon])[0];
    }
}
