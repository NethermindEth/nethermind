// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Shutter.Contracts;

public class KeyperSetManagerContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress) : CallableContract(transactionProcessor, abiEncoder, contractAddress), IKeyperSetManagerContract
{
    public Address GetKeyperSetAddress(BlockHeader blockHeader, in ulong index)
    {
        return (Address)Call(blockHeader, nameof(GetKeyperSetAddress), Address.Zero, [index])[0];
    }

    public ulong GetNumKeyperSets(BlockHeader blockHeader)
    {
        return (ulong)Call(blockHeader, nameof(GetNumKeyperSets), Address.Zero, [])[0];
    }

    public ulong GetKeyperSetIndexByBlock(BlockHeader blockHeader, in ulong blockNumber)
    {
        return (ulong)Call(blockHeader, nameof(GetKeyperSetIndexByBlock), Address.Zero, [blockNumber])[0];
    }
}
