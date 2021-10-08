using Nethermind.Core.Crypto;

namespace Ethereum.Test.Base
{
    public class PostStateJson
    {
        public IndexesJson Indexes { get; set; }
        public Keccak Hash { get; set; }
        public Keccak Logs { get; set; }
    }
}