namespace Nevermind.Evm
{
    public interface IVirtualMachine
    {
        (byte[] output, TransactionSubstate) Run(ExecutionEnvironment env, EvmState state, IBlockhashProvider blockhashProvider, IWorldStateProvider worldStateProvider, IStorageProvider storageProvider, IProtocolSpecification protocolSpecification);
    }
}