// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Merge.AuRa.Contracts;

public class WithdrawalContract : CallableContract, IWithdrawalContract
{
    public WithdrawalContract(
        ITransactionProcessor transactionProcessor,
        IAbiEncoder abiEncoder,
        Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress) { }

    public void ExecuteWithdrawals(BlockHeader blockHeader, UInt256 failedMaxCount, ulong[] amounts, Address[] addresses)
    {
        ArgumentNullException.ThrowIfNull(blockHeader);
        ArgumentNullException.ThrowIfNull(amounts);
        ArgumentNullException.ThrowIfNull(addresses);

        Call(blockHeader, "executeSystemWithdrawals", Address.SystemUser, UnlimitedGas, failedMaxCount, amounts, addresses);
    }
}
