// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa.Withdrawals;

public class WithdrawalProcessor : IWithdrawalProcessor
{
    private readonly ILogger _logger;
    private readonly AuRaParameters _auraParams;

    public WithdrawalProcessor(AuRaParameters auraParams, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _auraParams = auraParams ?? throw new ArgumentNullException(nameof(auraParams));
        _logger = logManager.GetClassLogger();
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        if (!spec.WithdrawalsEnabled)
            return;

        if (_logger.IsTrace) _logger.Trace($"Applying withdrawals for block {block}");

        if (block.Withdrawals is not null)
        {
            foreach (var withdrawal in block.Withdrawals)
            {
                if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)withdrawal.AmountInWei / (BigInteger)Unit.Ether:N3}GNO to account {withdrawal.Address}");

                //if (_auraParams.AccountExists(withdrawal.Address))
                //{
                //    _auraParams.AddToBalance(withdrawal.Address, withdrawal.Amount, spec);
                //}
                //else
                //{
                //    _auraParams.CreateAccount(withdrawal.Address, withdrawal.Amount);
                //}
            }
        }

        if (_logger.IsTrace) _logger.Trace($"Withdrawals applied for block {block}");
    }
}
