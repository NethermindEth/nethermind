using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace JsonTypes
{

    public partial class Transaction
    {
        //TODO: Figure out which are optional and which fields are missing
        public string? Input { get; set; }
        public string Gas { get; set; }
        public string? Nonce { get; set; }
        public Address? To { get; set; }
        public string? Value { get; set; }
        public string? V { get; set; }
        public string? R { get; set; }
        public string? S { get; set; }
        public string? SecretKey { get; set; }
        public string? ChainId { get; set; }
        public string? Type { get; set; }
        public string? MaxFeePerGas { get; set; }
        public string? GasPrice { get; set; }
        public string? MaxPriorityFeePerGas { get; set; }
        public object[]? AccessList { get; set; }
        public bool? Protected { get; set; }

        //public void convert(Transaction t)
        //{
        //    Nethermind.Core.Transaction tx = new Nethermind.Core.Transaction();
        //    //tx.BlockNumber = Bytes.FromHexString(t.BlockNumber).ToUInt256();
        //    tx.Data = Bytes.FromHexString(t.Input);
        //    //tx.GasLimit = Bytes.FromHexString(t.Gas).ToUInt256();
        //    tx.GasPrice = Bytes.FromHexString(t.GasPrice).ToUInt256();
        //    tx.Nonce = Bytes.FromHexString(t.Nonce).ToUInt256();
        //    tx.R = Bytes.FromHexString(t.R).ToUInt256();
        //    tx.S = Bytes.FromHexString(t.S).ToUInt256();
        //    tx.V = Bytes.FromHexString(t.V)[0];
        //    tx.Sender = new Address(byName.Value.Sender);
        //    tx.Value = Bytes.FromHexString(transactionJson.Value).ToUInt256();
        //    tx.To = string.IsNullOrEmpty(t.To) ? null : new Address(t.To);
        //}
    }
}
