// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.AuRa.Contracts;

public class WithdrawalContract : CallableContract, IWithdrawalContract
{
    public WithdrawalContract(
        ITransactionProcessor transactionProcessor,
        IAbiEncoder abiEncoder,
        Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress) { }

    public void Withdraw(BlockHeader blockHeader, ulong[] amounts, Address[] addresses)
    {
        ArgumentNullException.ThrowIfNull(blockHeader);
        ArgumentNullException.ThrowIfNull(amounts);
        ArgumentNullException.ThrowIfNull(addresses);

        Call(blockHeader, "withdraw", Address.SystemUser, UnlimitedGas, amounts, addresses);
    }
}
