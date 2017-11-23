using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public static class TransactionValidator
    {
        public static void Validate(
            Transaction transaction,
            Address sender,
            bool useEip155Rule = false,
            int chainIdValue = 0)
        {
            // TODO: since I recover myself I guess there is no need for it?
            //bool verified = Signer.Verify(
            //    sender,
            //    transaction,
            //    useEip155Rule,
            //    (ChainId)chainIdValue);
            bool verified = true;

            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            transaction.IsValid = IsWellFormed(transaction) && verified;
        }

        // TODO: add signature verification here...
        public static bool IsWellFormed(Transaction transaction)
        {
            return Validator.IsInP256(transaction.Nonce) &&
                   Validator.IsInP256(transaction.GasPrice) &&
                   Validator.IsInP256(transaction.GasLimit) &&
                   // ReSharper disable once IsExpressionAlwaysTrue
                   // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                   transaction.To != null || transaction.Init != null && // TODO: check tests where this is the case and still state changes (is the gas substracted?)
                   (transaction.To == null || transaction.To is Address) &&
                   Validator.IsInP256(transaction.Value) &&
                   // both null: transfer; data not null: message call; init not null: account creation
                   !(transaction.Data != null && transaction.Init != null);
            // also check if nonce is equal to sending account nonce
        }
    }
}