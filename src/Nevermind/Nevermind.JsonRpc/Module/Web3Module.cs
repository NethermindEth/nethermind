using System.Reflection;
using Nevermind.Blockchain;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class Web3Module : ModuleBase, IWeb3Module
    {
        public Web3Module(ILogger logger, IConfigurationProvider configurationProvider) : base(logger, configurationProvider)
        {
        }

        public ResultWrapper<string> web3_clientVersion()
        {
            var version = Assembly.GetAssembly(typeof(IBlockchainProcessor)).GetName().Version;
            var clientVersion = $"EthereumNet v{version}";
            Logger.Debug($"web3_clientVersion request, result: {clientVersion}");
            return ResultWrapper<string>.Success(clientVersion);
        }

        public ResultWrapper<Data> web3_sha3(Data data)
        {
            var keccak = Sha3(data);
            Logger.Debug($"web3_sha3 request, result: {keccak.ToJson()}");
            return ResultWrapper<Data>.Success(keccak);
        }
    }
}