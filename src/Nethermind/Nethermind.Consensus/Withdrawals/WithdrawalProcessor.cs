// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Witness;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Consensus.Withdrawals;

public class WithdrawalProcessor : IWithdrawalProcessor
{
    private readonly ILogger _logger;
    protected readonly WorldStateProvider _worldStateProvider;

    public WithdrawalProcessor(WorldStateProvider worldStateProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();
        _worldStateProvider = worldStateProvider ?? throw new ArgumentNullException(nameof(worldStateProvider));
    }

    public void ProcessWithdrawals(Block block, IBlockTracer blockTracer, IReleaseSpec spec)
    {
        IWorldState worldState = _worldStateProvider.GetWorldState(block);
        if (!spec.WithdrawalsEnabled)
            return;

        if (_logger.IsTrace) _logger.Trace($"Applying withdrawals for block {block}");

        IExecutionWitness witness = blockTracer.IsTracingAccessWitness
            ? new VerkleExecWitness(NullLogManager.Instance)
            : new NoExecWitness();

        if (block.Withdrawals is not null)
        {
            foreach (Withdrawal? withdrawal in block.Withdrawals)
            {
                if (_logger.IsTrace) _logger.Trace($"  {withdrawal.AmountInGwei} GWei to account {withdrawal.Address}");

                witness.AccessCompleteAccount(withdrawal.Address);
                // Consensus clients are using Gwei for withdrawals amount. We need to convert it to Wei before applying state changes https://github.com/ethereum/execution-apis/pull/354
                if (worldState.AccountExists(withdrawal.Address))
                {
                    worldState.AddToBalance(withdrawal.Address, withdrawal.AmountInWei, spec);
                }
                else
                {
                    worldState.CreateAccount(withdrawal.Address, withdrawal.AmountInWei);
                }
            }
        }
        if (_logger.IsTrace) _logger.Trace($"Withdrawals applied for block {block}");
        if (blockTracer.IsTracingAccessWitness) blockTracer.ReportAccessWitness(witness!);

    }
}
