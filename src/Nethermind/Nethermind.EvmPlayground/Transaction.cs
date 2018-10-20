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
        public string Gas { get; } = "0x6691b7";

        [JsonProperty("gasPrice", Order = 2)]
        public string GasPrice { get; } = "0x174876e800";

        [JsonProperty("to", Order = 3)]
        public string To { get; }

        [JsonProperty("data", Order = 4)]
        public string Data { get; }
    }
}