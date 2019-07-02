using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityTransaction
    {
        public Keccak Hash { get; set; }
        public BigInteger? Nonce { get; set; }
        public Keccak BlockHash { get; set; }
        public BigInteger? BlockNumber { get; set; }
        public BigInteger? TransactionIndex { get; set; }
        public Address From { get; set; }
        public Address To { get; set; }
        public BigInteger? Value { get; set; }
        public BigInteger? GasPrice { get; set; }
        public BigInteger? Gas { get; set; }
        public byte[] Input { get; set; }
        public byte[] Raw { get; set; }
        public Address Creates { get; set; }
        public byte[] PublicKey { get; set; }
        public int? ChainId { get; set; }
        public object Condition { get; set; }
        public byte[] R { get; set; }
        public byte[] S { get; set; }
        public BigInteger V { get; set; }
        public BigInteger StandardV { get; set; }

        public ParityTransaction()
        {
        }

        public ParityTransaction(Transaction transaction, byte[] raw, PublicKey publicKey,
            Keccak blockHash = null, BigInteger? blockNumber = null, int? txIndex = null)
        {
            Hash = transaction.Hash;
            Nonce = transaction.Nonce;
            BlockHash = blockHash;
            BlockNumber = blockNumber;
            TransactionIndex = txIndex;
            From = transaction.SenderAddress;
            To = transaction.To;
            Value = transaction.Value;
            GasPrice = transaction.GasPrice;
            Gas = transaction.GasLimit;
            Raw = raw;
            Input = Raw = transaction.Data ?? transaction.Init;
            PublicKey = publicKey.Bytes;
            ChainId = transaction.Signature.GetChainId;
            R = transaction.Signature.R;
            S = transaction.Signature.S;
            V = transaction.Signature.V;
            StandardV = transaction.Signature.RecoveryId;
        }
    }
}