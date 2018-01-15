using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public abstract class ModuleBase
    {
        protected readonly ILogger Logger;
        protected readonly IConfigurationProvider ConfigurationProvider;

        protected ModuleBase(ILogger logger, IConfigurationProvider configurationProvider)
        {
            Logger = logger;
            ConfigurationProvider = configurationProvider;
        }

        protected Data Sha3(Data data)
        {
            var keccak = Keccak.Compute((byte[])data.Value);
            var keccakValue = keccak.ToString();
            return new Data(keccakValue);
        }

        public virtual void Initialize()
        {
        }
    }
}