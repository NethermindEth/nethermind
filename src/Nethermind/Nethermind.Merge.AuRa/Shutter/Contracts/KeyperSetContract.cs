// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class KeyperSetContract : CallableContract, IKeyperSetContract
{
    private const string isFinalized = "isFinalized";
    private const string getThreshold = "getThreshold";
    private const string getMembers = "getMembers";

    public KeyperSetContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
    }

    public bool IsFinalized(BlockHeader blockHeader)
    {
        return (bool)Call(blockHeader, isFinalized, Address.Zero, [])[0];
    }

    public ulong GetThreshold(BlockHeader blockHeader)
    {
        return (ulong)Call(blockHeader, getThreshold, Address.Zero, [])[0];
    }

    public Address[] GetMembers(BlockHeader blockHeader)
    {
        return (Address[])Call(blockHeader, getMembers, Address.Zero, [])[0];
    }
}
