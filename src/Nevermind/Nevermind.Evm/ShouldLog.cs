namespace Nevermind.Evm
{
    public static class ShouldLog
    {
        public static volatile bool Evm = false; // marked volatile to make ReSharper think it is not a const
    }
}