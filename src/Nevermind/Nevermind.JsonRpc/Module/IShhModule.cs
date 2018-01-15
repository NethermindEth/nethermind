using System.Collections.Generic;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public interface IShhModule : IModule
    {
        ResultWrapper<bool> shh_post(WhisperPostMessage message);
        ResultWrapper<Data> shh_newIdentity();
        ResultWrapper<bool> shh_hasIdentity(Data address);
        ResultWrapper<Quantity> shh_newFilter(WhisperFilter filter);
        ResultWrapper<bool> shh_uninstallFilter(Quantity filterId);
        ResultWrapper<IEnumerable<WhisperMessage>> shh_getFilterChanges(Quantity filterId);
        ResultWrapper<IEnumerable<WhisperMessage>> shh_getMessages(Quantity filterId);
    }
}