namespace Nevermind.Core.Validators
{
    public static class TransactionValidator
    {
        public static bool IsValid(Transaction transaction)
        {
            return Validator.IsInP256(transaction.Nonce) &&
                   Validator.IsInP256(transaction.GasPrice) &&
                   Validator.IsInP256(transaction.GasLimit) &&
                   // ReSharper disable once IsExpressionAlwaysTrue
                   // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                   (transaction.To == null || transaction.To is Address) &&
                   Validator.IsInP256(transaction.Value) &&
                   (transaction.Signature == null || SignatureValidator.IsValid(transaction.Signature)) &&
                   // both null: transfer; data not null: message call; init not null: account creation
                   !(transaction.Data != null && transaction.Init != null);
        }
    }
}