using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public abstract class ModuleBase
    {
        protected readonly ILogger Logger;

        protected ModuleBase(ILogger logger)
        {
            Logger = logger;
        }

        protected Data Sha3(Data data)
        {
            var hexBytes = data.Value.ToBytes();
            var keccak = Keccak.Compute(hexBytes);
            var keccakValue = keccak.ToString();
            return new Data(keccakValue);
        }
    }
}