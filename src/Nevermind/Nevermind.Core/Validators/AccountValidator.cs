using Nevermind.Core.Encoding;

namespace Nevermind.Core.Validators
{
    public static class AccountValidator
    {
        public static bool IsValid(Account account)
        {
            return Validator.IsInP256(account.Nonce) &&
                   Validator.IsInP256(account.Balance) &&
                   // ReSharper disable once IsExpressionAlwaysTrue
                   account.CodeHash is Keccak &&
                   // ReSharper disable once IsExpressionAlwaysTrue
                   account.StorageRoot is Keccak;
        }
    }
}