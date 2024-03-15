using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Evm.JsonTypes
{
    public class TransactionInfo
    {
        public byte[]? Input { get; set; }
        public long Gas { get; set; }
        public string? Hash { get; set; }
        public UInt256 Nonce { get; set; }
        public Address To { get; set; }
        public UInt256 Value { get; set; }
        public ulong V { get; set; }
        public byte[]? R { get; set; }
        public byte[]? S { get; set; }
        public byte[]? SecretKey { get; set; }
        public ulong? ChainId { get; set; }
        public TxType Type { get; set; } = TxType.Legacy;
        public UInt256? MaxFeePerGas { get; set; }
        public UInt256 GasPrice { get; set; }
        public UInt256? MaxPriorityFeePerGas { get; set; }
        public AccessListItemJson[]? AccessList { get; set; }
        public bool? Protected { get; set; }

        public Transaction ConvertToTx()
        {
            TransactionBuilder<Transaction> transactionBuilder = new();

            transactionBuilder.WithValue(Value);
            if (Input != null)
            {
                transactionBuilder.WithData(Input);
            }
            transactionBuilder.WithTo(To);
            transactionBuilder.WithNonce(Nonce);
            transactionBuilder.WithGasLimit(Gas);
            transactionBuilder.WithType(Type);
            transactionBuilder.WithGasPrice(GasPrice);
            AccessList.Builder builder = new();
            JsonToEthereumTest.ProcessAccessList(AccessList, builder);
            transactionBuilder.WithAccessList(builder.Build());
            if (MaxFeePerGas.HasValue)
            {
                transactionBuilder.WithMaxFeePerGas(MaxFeePerGas.Value);
            }
            if (MaxPriorityFeePerGas.HasValue)
            {
                transactionBuilder.WithMaxPriorityFeePerGas(MaxPriorityFeePerGas.Value);
            }

            if (ChainId.HasValue)
            {
                transactionBuilder.WithChainId(ChainId.Value);
            }
            if (SecretKey != null)
            {
                var privateKey = new PrivateKey(SecretKey);
                transactionBuilder.WithSenderAddress(privateKey.Address);
                transactionBuilder.Signed(privateKey, Protected ?? true);
            }
            else
            {
                transactionBuilder.WithSignature(new Signature(R, S, V));
            }

            return transactionBuilder.TestObject;
        }
    }
}
