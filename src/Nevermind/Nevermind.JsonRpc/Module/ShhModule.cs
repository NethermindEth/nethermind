using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class ShhModule : ModuleBase, IShhModule
    {
        public ShhModule(ILogger logger, IConfigurationProvider configurationProvider) : base(logger, configurationProvider)
        {
        }

        public ResultWrapper<bool> shh_post(WhisperPostMessage message)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<Data> shh_newIdentity()
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<bool> shh_hasIdentity(Data address)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<Quantity> shh_newFilter(WhisperFilter filter)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<bool> shh_uninstallFilter(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<IEnumerable<WhisperMessage>> shh_getFilterChanges(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<IEnumerable<WhisperMessage>> shh_getMessages(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }
    }
}