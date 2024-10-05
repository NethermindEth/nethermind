// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Shutter.Contracts;

public class KeyperSetContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress) : CallableContract(transactionProcessor, abiEncoder, contractAddress), IKeyperSetContract
{
    public bool IsFinalized(BlockHeader blockHeader)
    {
        return (bool)Call(blockHeader, nameof(IsFinalized), Address.Zero, [])[0];
    }

    public ulong GetThreshold(BlockHeader blockHeader)
    {
        return (ulong)Call(blockHeader, nameof(GetThreshold), Address.Zero, [])[0];
    }

    public Address[] GetMembers(BlockHeader blockHeader)
    {
        return (Address[])Call(blockHeader, nameof(GetMembers), Address.Zero, [])[0];
    }
}
