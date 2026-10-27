// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Experimental.Abi;

namespace Nethermind.Shutter.Contracts;

public class KeyperSetManagerContract(ITransactionProcessor transactionProcessor, Address contractAddress) : CallableContract(transactionProcessor, contractAddress), IKeyperSetManagerContract
{
    private static readonly ContractFunction<ulong, Address> GetKeyperSetAddressFunction = new AbiSignature("getKeyperSetAddress").With(AbiType.UInt64).Returns(AbiType.Address);
    private static readonly ContractFunction<ValueTuple, ulong> GetNumKeyperSetsFunction = new AbiSignature("getNumKeyperSets").With().Returns(AbiType.UInt64);
    private static readonly ContractFunction<ulong, ulong> GetKeyperSetIndexByBlockFunction = new AbiSignature("getKeyperSetIndexByBlock").With(AbiType.UInt64).Returns(AbiType.UInt64);

    public Address GetKeyperSetAddress(BlockHeader blockHeader, in ulong index) => Call(blockHeader, GetKeyperSetAddressFunction, Address.Zero, index);

    public ulong GetNumKeyperSets(BlockHeader blockHeader) => Call(blockHeader, GetNumKeyperSetsFunction, Address.Zero, default);

    public ulong GetKeyperSetIndexByBlock(BlockHeader blockHeader, in ulong blockNumber) => Call(blockHeader, GetKeyperSetIndexByBlockFunction, Address.Zero, blockNumber);
}
