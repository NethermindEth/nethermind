using Newtonsoft.Json;

namespace Nevermind.JsonRpc.DataModel
{
    public class Error
    {
        [JsonProperty(PropertyName = "code")]
        public int Code { get; set; }
        [JsonProperty(PropertyName = "message")]
        public string Message { get; set; }
        [JsonProperty(PropertyName = "data")]
        public string Data { get; set; }
    }
}