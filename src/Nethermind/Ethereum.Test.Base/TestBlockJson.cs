using Newtonsoft.Json;

namespace Ethereum.Test.Base
{
    public class TestBlockJson
    {
        public TestBlockHeaderJson BlockHeader { get; set; }
        public TestBlockHeaderJson[] UncleHeaders { get; set; }
        public string Rlp { get; set; }
        public TransactionJson[] Transactions { get; set; }
        [JsonProperty("expectExceptionALL")]
        public string ExpectedException { get; set; }
    }
}