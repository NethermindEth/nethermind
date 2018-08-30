using Nethermind.Core;

namespace Nethermind.JsonRpc.DataModel
{
    public class CompilerParameters : IJsonRpcRequest
    {
        private readonly IJsonSerializer _jsonSerializer;

        public CompilerParameters()
        {
            _jsonSerializer = new UnforgivingJsonSerializer();
        }

        public string Contract { get; set; }
        public string EvmVersion { get; set; }
        public bool Optimize { get; set; }
        public uint? Runs { get; set; }
        
        public void FromJson(string jsonValue)
        {
            var jsonObj = new
            {
                contract = string.Empty,
                evmversion = string.Empty,
                optimize = new bool(),
                runs = new uint()
            };

            var compileParameters = _jsonSerializer.DeserializeAnonymousType(jsonValue, jsonObj);
            Contract = compileParameters.contract;
            EvmVersion = compileParameters.evmversion ?? "byzantium";
            Optimize = compileParameters.optimize;
            Runs = compileParameters.runs;
        }

        public string ToJson()
        {
            return _jsonSerializer.Serialize(this);
        }
    }
}