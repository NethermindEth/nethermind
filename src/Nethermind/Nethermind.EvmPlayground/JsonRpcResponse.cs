using System.Numerics;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.EvmPlayground
{
    public class JsonRpcResponse
    {
        [JsonProperty(PropertyName = "jsonrpc", Order = 1)]
        public string Jsonrpc { get; set; }

        [JsonProperty(PropertyName = "result", Order = 2)]
        public string Result { get; set; }

        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Ignore, Order = 3)]
        public string Error { get; set; }

        [JsonProperty(PropertyName = "id", Order = 0)]
        public UInt256 Id { get; set; }
    }
    
    public class JsonRpcResponse<T>
    {
        [JsonProperty(PropertyName = "jsonrpc", Order = 1)]
        public string Jsonrpc { get; set; }
        [JsonProperty(PropertyName = "result", Order = 2)]
        public T Result { get; set; }
        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Ignore, Order = 3)]
        public string Error { get; set; }
        [JsonProperty(PropertyName = "id", Order = 0)]
        public UInt256 Id { get; set; }
    }
}