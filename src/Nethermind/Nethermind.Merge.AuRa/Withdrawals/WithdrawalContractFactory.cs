// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Contracts;

namespace Nethermind.Merge.AuRa.Withdrawals;

public class WithdrawalContractFactory : IWithdrawalContractFactory
{
    private readonly Address _contractAddress;

    public WithdrawalContractFactory(AuRaChainSpecEngineParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        _contractAddress = parameters.WithdrawalContractAddress;
    }

    public IWithdrawalContract Create(ITransactionProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);

        return new WithdrawalContract(processor, _contractAddress);
    }
}
