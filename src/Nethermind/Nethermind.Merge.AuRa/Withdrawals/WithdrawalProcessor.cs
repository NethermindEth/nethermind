// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Contracts;

namespace Nethermind.Merge.AuRa.Withdrawals;

public class WithdrawalProcessor : IWithdrawalProcessor
{
    private readonly IWithdrawalContract _contract;
    private readonly UInt256 _failedWithdrawalsMaxCount = 4;
    private readonly ILogger _logger;

    public WithdrawalProcessor(IWithdrawalContract contract, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _logger = logManager.GetClassLogger();
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        if (!spec.WithdrawalsEnabled || block.Withdrawals is null) // The second check seems redundant
            return;

        if (_logger.IsTrace) _logger.Trace($"Applying withdrawals for block {block}");

        var count = block.Withdrawals.Length;
        var amounts = new ulong[count];
        var addresses = new Address[count];

        for (var i = 0; i < count; i++)
        {
            var withdrawal = block.Withdrawals[i];

            addresses[i] = withdrawal.Address;
            amounts[i] = withdrawal.AmountInGwei;

            if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)withdrawal.AmountInWei / (BigInteger)Unit.Ether:N3}GNO to account {withdrawal.Address}");
        }

        try
        {
            _contract.ExecuteWithdrawals(block.Header, _failedWithdrawalsMaxCount, amounts, addresses);
        }
        catch (Exception ex)
        {
            throw new InvalidBlockException(block, ex);
        }

        if (_logger.IsTrace) _logger.Trace($"Withdrawals applied for block {block}");
    }
}
