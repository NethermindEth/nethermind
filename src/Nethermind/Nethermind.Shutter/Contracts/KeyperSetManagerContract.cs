// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Shutter.Contracts;

public class KeyperSetManagerContract(ITransactionProcessor transactionProcessor, Address contractAddress) : CallableContract(transactionProcessor, contractAddress), IKeyperSetManagerContract
{
    internal static readonly ContractFunction<ulong, Address> GetKeyperSetAddressFunction = new AbiSignature("getKeyperSetAddress").With(AbiType.UInt64).Returns(AbiType.Address);
    internal static readonly ContractFunction<ulong, ulong> GetKeyperSetIndexByBlockFunction = new AbiSignature("getKeyperSetIndexByBlock").With(AbiType.UInt64).Returns(AbiType.UInt64);

    public Address GetKeyperSetAddress(BlockHeader blockHeader, in ulong index) => Call(blockHeader, GetKeyperSetAddressFunction, Address.Zero, index);

    public ulong GetKeyperSetIndexByBlock(BlockHeader blockHeader, in ulong blockNumber) => Call(blockHeader, GetKeyperSetIndexByBlockFunction, Address.Zero, blockNumber);
}
