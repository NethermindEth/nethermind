using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public static class AccountValidator
    {
        public static bool IsValid(Account account)
        {
            return Validator.IsInP256(account.Nonce) &&
                   Validator.IsInP256(account.Balance);
        }
    }
}