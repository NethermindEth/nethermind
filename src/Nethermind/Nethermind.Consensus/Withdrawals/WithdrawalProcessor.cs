// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Withdrawals;

public class WithdrawalProcessor : IWithdrawalProcessor
{
    private readonly ILogger _logger;
    private readonly IStateProvider _stateProvider;

    public WithdrawalProcessor(IStateProvider stateProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        if (!spec.WithdrawalsEnabled)
            return;

        if (_logger.IsTrace) _logger.Trace($"Applying withdrawals for block {block}");

        if (block.Withdrawals != null)
        {
            foreach (var withdrawal in block.Withdrawals)
            {
                if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)withdrawal.Amount / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} to account {withdrawal.Address}");

                if (_stateProvider.AccountExists(withdrawal.Address))
                {
                    _stateProvider.AddToBalance(withdrawal.Address, withdrawal.Amount, spec);
                }
                else
                {
                    _stateProvider.CreateAccount(withdrawal.Address, withdrawal.Amount);
                }
            }
        }

        if (_logger.IsTrace) _logger.Trace($"Withdrawals applied for block {block}");
    }
}
