// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Merge.AuRa.Withdrawals;

public class WithdrawalProcessor : IWithdrawalProcessor
{
    private readonly IWithdrawalContract _contract;
    private readonly ILogger _logger;

    public WithdrawalProcessor(IWithdrawalContract contract, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _logger = logManager.GetClassLogger();
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        if (!spec.WithdrawalsEnabled)
            return;

        if (_logger.IsTrace) _logger.Trace($"Applying withdrawals for block {block}");

        if (block.Withdrawals is not null) // This check looks redundant
        {
            var count = block.Withdrawals.Length;
            var amounts = ArrayPool<ulong>.Shared.Rent(count);
            var addresses = ArrayPool<Address>.Shared.Rent(count);

            for (var i = 0; i < count; i++)
            {
                var withdrawal = block.Withdrawals[i];

                addresses[i] = withdrawal.Address;
                amounts[i] = withdrawal.AmountInGwei;

                if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)withdrawal.AmountInWei / (BigInteger)Unit.Ether:N3}GNO to account {withdrawal.Address}");
            }

            // TODO: check for a failure to invalidate block
            _contract.Withdraw(block.Header, amounts, addresses);

            ArrayPool<ulong>.Shared.Return(amounts);
            ArrayPool<Address>.Shared.Return(addresses);
        }

        if (_logger.IsTrace) _logger.Trace($"Withdrawals applied for block {block}");
    }
}
