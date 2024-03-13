using System.ComponentModel;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonTypes
{
    public class Hash256ArrayConverter : JsonConverter<Hash256[]>
    {
        public override Hash256[] ReadJson(JsonReader reader, Type objectType, Hash256[] existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);

            if (token.Type == JTokenType.Object)
            {
                // If the token is an object, assume it's a dictionary
                var dictionary = token.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();
                var hashArray = dictionary.Values.Select(value => new Hash256(value)).ToArray();
                return hashArray;
            }

            // Fallback to an empty array if not an object
            return new Hash256[0];
        }

        public override void WriteJson(JsonWriter writer, Hash256[] value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
    public partial class Env
    {
        public Address CurrentCoinbase { get; set; } = Address.Zero;
        public string CurrentGasLimit { get; set; } = "0x3000000000";
        public ulong CurrentTimestamp { get; set; } = 0;
        public long CurrentNumber { get; set; } = 0;

        public string[] Withdrawals { get; set; } = [];

        public Hash256 PreviousHash { get; set; } = Keccak.Zero;
        public UInt64 CurrentDataGasUsed { get; set; } = 0;
        public string ParentTimestamp { get; set; } = "0";
        public string ParentDifficulty { get; set; } = "0";
        public string CurrentBaseFee { get; set; } = "0x";
        public string CurrentDifficulty { get; set; } = "0";
        public Hash256 ParentUncleHash { get; set; } = Keccak.Zero;
        public Hash256 ParentBeaconBlockRoot { get; set; } = Keccak.Zero;
        public Hash256 CurrentRandom { get; set; } = Keccak.Zero;
        public UInt256 ParentBaseFee { get; set; } = UInt256.Zero;
        public string? ParentGasUsed { get; set; }
        public string? ParentGasLimit { get; set; }
        public ulong? ParentExcessBlobGas { get; set; }
        public ulong? ParentBlobGasUsed { get; set; }
        public Dictionary<string, Hash256> BlockHashes { get; set; } = [];


    }
}
