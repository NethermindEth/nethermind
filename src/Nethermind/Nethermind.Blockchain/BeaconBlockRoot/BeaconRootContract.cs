// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

public interface IBeaconRootContract
{
    void Invoke(BlockHeader blockHeader, IReleaseSpec spec);
}
public sealed class BeaconRootContract : IBeaconRootContract
{
    private readonly ITransactionProcessor _transactionProcessor;
    public BeaconRootContract(ITransactionProcessor transactionProcessor, Address contractAddress)
    {
        _transactionProcessor = transactionProcessor;
    }

    public void Invoke(BlockHeader blockHeader, IReleaseSpec spec)
    {
        if (!spec.IsBeaconBlockRootAvailable ||
            blockHeader.IsGenesis ||
            blockHeader.ParentBeaconBlockRoot is null) return;

        var transaction = new Transaction()
        {
            Value = UInt256.Zero,
            Data = blockHeader.ParentBeaconBlockRoot.Bytes.ToArray(),
            To = Address.FromNumber(0x0b),
            SenderAddress = Address.SystemUser,
            GasLimit = long.MaxValue, // ToDO Unlimited gas will be probably changed to 30mln
            GasPrice = UInt256.Zero,
        };

        transaction.Hash = transaction.CalculateHash();

        _transactionProcessor.Execute(transaction, blockHeader, NullTxTracer.Instance);
    }
}
