// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts;

public interface IWithdrawalContract : IActivatedAtForkId
{
    void Withdraw(BlockHeader blockHeader, ulong[] amounts, Address[] addresses);
}

public class WithdrawalContract : CallableContract, IWithdrawalContract
{
    public WithdrawalContract(
        ITransactionProcessor transactionProcessor,
        IAbiEncoder abiEncoder,
        Address contractAddress,
        ForkActivation forkId)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        Activation = forkId;
    }

    public void Withdraw(BlockHeader blockHeader, ulong[] amounts, Address[] addresses)
    {
        ArgumentNullException.ThrowIfNull(blockHeader);
        ArgumentNullException.ThrowIfNull(amounts);
        ArgumentNullException.ThrowIfNull(addresses);

        ((IActivatedAtForkId)this).EnsureActivated(blockHeader);

        Call(blockHeader, "withdraw", Address.SystemUser, UnlimitedGas, amounts, addresses);
    }

    public ForkActivation Activation { get; }
}
