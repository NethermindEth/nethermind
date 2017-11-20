namespace Nevermind.Evm
{
    // TODO: refactor
    public enum ExecutionType
    {
        Transaction,
        Call,
        Callcode,
        Create,
        Precompile,
        DirectPrecompile,
        DirectCreate,
    }
}