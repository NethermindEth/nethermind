using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Test.Builders
{
    public class TransactionReceiptContextBuilder
    {
        private TransactionReceipt _receipt = Core.Test.Builders.Build.A.TransactionReceipt.TestObject;
        private UInt256 _logIndex = UInt256.Zero;
        private UInt256 _blockNumber = UInt256.Zero;
        private Keccak _blockHash = Keccak.Zero;
        private UInt256 _transactionIndex = UInt256.Zero;
        private Keccak _transactionHash = Keccak.Zero;

        private TransactionReceiptContextBuilder()
        {
        }

        public static TransactionReceiptContextBuilder New()
            => new TransactionReceiptContextBuilder();

        public TransactionReceiptContextBuilder WithReceipt(TransactionReceipt receipt)
        {
            _receipt = receipt;

            return this;
        }

        public TransactionReceiptContextBuilder WithLogIndex(UInt256 index)
        {
            _logIndex = index;

            return this;
        }

        public TransactionReceiptContextBuilder WithBlockNumber(UInt256 number)
        {
            _blockNumber = number;

            return this;
        }

        public TransactionReceiptContextBuilder WithBlockHash(Keccak hash)
        {
            _blockHash = hash;

            return this;
        }

        public TransactionReceiptContextBuilder WithTransactionIndex(UInt256 index)
        {
            _transactionIndex = index;

            return this;
        }

        public TransactionReceiptContextBuilder WithTransactionHash(Keccak hash)
        {
            _transactionHash = hash;

            return this;
        }

        public TransactionReceiptContext Build()
            => new TransactionReceiptContext(_receipt, _logIndex,
                _blockNumber, _blockHash, _transactionIndex, _transactionHash);
    }
}