using System.Collections.Generic;
using System.Text;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc
{
    public interface IConfigurationProvider
    {
        IDictionary<ErrorType, int> ErrorCodes { get; }
        string JsonRpcVersion { get; }
        IEnumerable<ModuleType> EnabledModules { get; set; }
        Encoding MessageEncoding { get; }
        string SignatureTemplate { get; }
    }
}