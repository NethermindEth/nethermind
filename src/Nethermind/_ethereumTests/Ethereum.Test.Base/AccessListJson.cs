using Nethermind.Core;
using Newtonsoft.Json;

namespace Ethereum.Test.Base
{
    public class AccessListItemJson
    {
        [JsonProperty("address")]
        public Address Address { get; set; }
        
        [JsonProperty("storageKeys")]
        public byte[][] StorageKeys { get; set; }
    }
}
