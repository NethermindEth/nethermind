using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismTxValidator : ITxValidator
{
    private readonly ITxValidator _txValidator;

    public OptimismTxValidator(ITxValidator txValidator)
    {
        _txValidator = txValidator;
    }

    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.Type == TxType.DepositTx || _txValidator.IsWellFormed(transaction, releaseSpec);
}
