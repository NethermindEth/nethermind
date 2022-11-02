using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Withdrawals;

public class ProductionWithdrawalApplier : IWithdrawalApplier
{
    private readonly IWithdrawalApplier _validationWithdrawalApplier;

    public ProductionWithdrawalApplier(IWithdrawalApplier validationWithdrawalApplier)
    {
        _validationWithdrawalApplier = validationWithdrawalApplier;
    }

    public void ApplyWithdrawals(Block block, IReleaseSpec spec)
    {
        _validationWithdrawalApplier.ApplyWithdrawals(block, spec);

        if (spec.IsEip4895Enabled)
        {
            block.Header.WithdrawalsRoot = block.Withdrawals.Length == 0
                ? Keccak.EmptyTreeHash : new WithdrawalTrie(block.Withdrawals!).RootHash;
        }
    }
}
