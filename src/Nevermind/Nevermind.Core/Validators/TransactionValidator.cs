using Nevermind.Core.Signing;

namespace Nevermind.Core.Validators
{
    public static class TransactionValidator
    {
        public static bool IsValid(
            Transaction transaction,
            Address sender,
            bool useEip155Rule = false,
            int chainIdValue = 0)
        {
            bool verified = Signer.Verify(
                sender,
                transaction,
                useEip155Rule,
                (ChainId)chainIdValue);

            return IsWellFormed(transaction) && verified;
        }

        // TODO: add signature verification here...
        public static bool IsWellFormed(Transaction transaction)
        {
            return Validator.IsInP256(transaction.Nonce) &&
                   Validator.IsInP256(transaction.GasPrice) &&
                   Validator.IsInP256(transaction.GasLimit) &&
                   // ReSharper disable once IsExpressionAlwaysTrue
                   // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                   (transaction.To == null || transaction.To is Address) &&
                   Validator.IsInP256(transaction.Value) &&
                   // both null: transfer; data not null: message call; init not null: account creation
                   !(transaction.Data != null && transaction.Init != null);
            // also check if nonce is equal to sending account nonce
        }
    }
}