using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc
{
    public class ConfigurationProvider : IConfigurationProvider
    {
        public ConfigurationProvider()
        {
            EnabledModules = Enum.GetValues(typeof(ModuleType)).OfType<ModuleType>();
        }

        public IDictionary<ErrorType, int> ErrorCodes => new Dictionary<ErrorType, int>
        {
            { ErrorType.ParseError, -32700 },
            { ErrorType.InvalidRequest, -32700 },
            { ErrorType.MethodNotFound, -32700 },
            { ErrorType.InvalidParams, -32700 },
            { ErrorType.InternalError, -32700 }
        };

        public string JsonRpcVersion => "2.0";
        public IEnumerable<ModuleType> EnabledModules { get; set; }
        public Encoding MessageEncoding => Encoding.UTF8;
        public string SignatureTemplate => "\x19Ethereum Signed Message:\n{0}{1}";
    }
}