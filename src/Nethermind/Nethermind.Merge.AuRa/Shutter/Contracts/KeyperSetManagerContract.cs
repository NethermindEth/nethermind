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

    public KeyperSetManagerContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
    }

    public (Address, ulong) GetKeyperSetAddress(BlockHeader blockHeader, in ulong index)
    {
        return ((Address, ulong))Call(blockHeader, getKeyperSetAddress, Address.Zero, [index])[0];
    }
}
