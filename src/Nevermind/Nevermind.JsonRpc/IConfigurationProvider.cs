using System.Collections.Generic;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc
{
    public interface IConfigurationProvider
    {
        IDictionary<ErrorType, int> ErrorCodes { get; }
        string JsonRpcVersion { get; }
        IEnumerable<ModuleType> EnabledModules { get; set; }
    }
}