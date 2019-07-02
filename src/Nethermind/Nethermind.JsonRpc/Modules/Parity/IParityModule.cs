namespace Nethermind.JsonRpc.Modules.Parity
{
    public interface IParityModule : IModule
    {
        ResultWrapper<ParityTransaction[]> parity_pendingTransactions();
    }
}