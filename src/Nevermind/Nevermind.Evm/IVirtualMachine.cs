namespace Nevermind.Evm
{
    public interface IVirtualMachine
    {
        (byte[] output, TransactionSubstate) Run(EvmState state);
    }
}