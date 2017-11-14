namespace Nevermind.Evm
{
    public static class ShouldLog
    {
        public static volatile bool TransactionProcessor = true; // marked volatile to make ReSharper think it is not a const
        public static volatile bool Evm = true; // marked volatile to make ReSharper think it is not a const
        //public const bool TransactionProcessor = false; // no volatile for performance testing
        //public const bool Evm = false; // no volatile for performance testing
    }
}