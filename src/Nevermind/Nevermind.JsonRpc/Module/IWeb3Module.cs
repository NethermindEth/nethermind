namespace Nevermind.JsonRpc.Module
{
    public interface IWeb3Module : IModule
    {
        string web3_clientVersion();
        string web3_sha3(string data);
    }
}