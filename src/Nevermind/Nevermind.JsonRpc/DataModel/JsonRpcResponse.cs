using Newtonsoft.Json;

namespace Nevermind.JsonRpc.DataModel
{
    public class JsonRpcResponse
    {
        [JsonProperty(PropertyName = "jsonrpc")]
        public string Jsonrpc { get; set; }
        [JsonProperty(PropertyName = "result")]
        public object Result { get; set; }
        [JsonProperty(PropertyName = "error")]
        public Error Error { get; set; }
        [JsonProperty(PropertyName = "id")]
        public string Id { get; set; }
    }
}