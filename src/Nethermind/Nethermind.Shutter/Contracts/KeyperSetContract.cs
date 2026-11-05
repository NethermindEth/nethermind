// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Shutter.Contracts;

public class KeyperSetContract(ITransactionProcessor transactionProcessor, Address contractAddress) : CallableContract(transactionProcessor, contractAddress), IKeyperSetContract
{
    internal static readonly ContractFunction<ValueTuple, bool> IsFinalizedFunction = new AbiSignature("isFinalized").With().Returns(AbiType.Bool);
    internal static readonly ContractFunction<ValueTuple, ulong> GetThresholdFunction = new AbiSignature("getThreshold").With().Returns(AbiType.UInt64);
    internal static readonly ContractFunction<ValueTuple, Address[]> GetMembersFunction = new AbiSignature("getMembers").With().Returns(AbiType.Array(AbiType.Address));

    public bool IsFinalized(BlockHeader blockHeader) => Call(blockHeader, IsFinalizedFunction, Address.Zero, default);

    public ulong GetThreshold(BlockHeader blockHeader) => Call(blockHeader, GetThresholdFunction, Address.Zero, default);

    public Address[] GetMembers(BlockHeader blockHeader) => Call(blockHeader, GetMembersFunction, Address.Zero, default);
}
