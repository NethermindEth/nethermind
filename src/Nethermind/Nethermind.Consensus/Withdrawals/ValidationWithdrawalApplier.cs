using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Withdrawals;

public class ValidationWithdrawalApplier : IWithdrawalApplier
{
    private readonly IStateProvider _stateProvider;
    private readonly ILogger _logger;

    public ValidationWithdrawalApplier(
        IStateProvider stateProvider,
        ILogManager logManager)
    {
        _stateProvider = stateProvider;
        _logger = logManager.GetClassLogger();
    }

    public void ApplyWithdrawals(Block block, IReleaseSpec spec)
    {
        if (!spec.IsEip4895Enabled)
            return;

        if (_logger.IsTrace) _logger.Trace($"Applying withdrawals for block {block}");

        if (block.Withdrawals != null)
        {
            foreach (var withdrawal in block.Withdrawals)
            {
                if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)withdrawal.Amount / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} to account {withdrawal.Recipient}");

                if (_stateProvider.AccountExists(withdrawal.Recipient))
                {
                    _stateProvider.AddToBalance(withdrawal.Recipient, withdrawal.Amount, spec);
                }
                else
                {
                    _stateProvider.CreateAccount(withdrawal.Recipient, withdrawal.Amount);
                }
            }
        }

        if (_logger.IsTrace) _logger.Trace($"Withdrawals applied for block {block}");
    }
}
