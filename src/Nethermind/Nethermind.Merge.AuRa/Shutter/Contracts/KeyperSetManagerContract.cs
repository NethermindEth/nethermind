// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class KeyperSetManagerContract : CallableContract, IKeyperSetManagerContract
{
    private const string getKeyperSetAddress = "getEonKey";
    private const string getNumKeyperSets = "getNumKeyperSets";

    public KeyperSetManagerContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
    }

    public (Address, ulong) GetKeyperSetAddress(BlockHeader blockHeader, in ulong index)
    {
        return ((Address, ulong))Call(blockHeader, getKeyperSetAddress, Address.Zero, [index])[0];
    }

    public ulong GetNumKeyperSets(BlockHeader blockHeader)
    {
        return (ulong)Call(blockHeader, getNumKeyperSets, Address.Zero, [])[0];
    }
}
