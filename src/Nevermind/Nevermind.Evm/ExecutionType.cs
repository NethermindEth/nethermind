namespace Nevermind.Evm
{
    public enum ExecutionType
    {
        Transaction,
        Call,
        Callcode,
        Create,
        Precompile,
        DirectPrecompile,
    }
}