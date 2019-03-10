using Newtonsoft.Json;

namespace Nethermind.EvmPlayground
{
    public class Transaction
    {
        public Transaction(byte[] sender, byte[] data)
        {
            From = sender.ToHexString(true);
            Data = data.ToHexString(true);
        }
       
        [JsonProperty("from", Order = 0)]
        public string From { get; }

        [JsonProperty("gas", Order = 1)]
        public string Gas { get; } = "0xF4240";

        [JsonProperty("gasPrice", Order = 2)]
        public string GasPrice { get; } = "0x4A817C800";

        [JsonProperty("to", Order = 3)]
        public string To { get; }

        [JsonProperty("data", Order = 4)]
        public string Data { get; }
    }
}