using System.Collections.Generic;

namespace Ethereum.Test.Base
{
    public class AccountStateJson
    {
        public string Balance { get; set; }
        public string Code { get; set; }
        public string Nonce { get; set; }
        public Dictionary<string, string> Storage { get; set; }
    }
}