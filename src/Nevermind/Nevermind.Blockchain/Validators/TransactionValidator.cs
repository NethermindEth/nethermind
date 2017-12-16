using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public static class TransactionValidator
    {
        // TODO: add signature verification here...
        public static bool IsWellFormed(Transaction transaction)
        {
            return Validator.IsInP256(transaction.Nonce) &&
                   Validator.IsInP256(transaction.GasPrice) &&
                   transaction.GasLimit <= long.MaxValue &&
                   transaction.GasLimit >= 21000 &&
                   // ReSharper disable once IsExpressionAlwaysTrue
                   // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                   transaction.To != null || transaction.Init != null && // TODO: check tests where this is the case and still state changes (is the gas substracted?)
                   (transaction.To == null || transaction.To is Address) &&
                   Validator.IsInP256(transaction.Value) &&
                   // both null: transfer; data not null: message call; init not null: account creation
                   !(transaction.Data != null && transaction.Init != null);
            // TODO: also check if nonce is equal to sending account nonce
        }
    }
}