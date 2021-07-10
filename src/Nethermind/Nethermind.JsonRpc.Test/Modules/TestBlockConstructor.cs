using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.JsonRpc.Test.Modules
{
    public static class TestBlockConstructor
    {
        public static Block GetTestBlockA()
        {
            Transaction[] transactions = {
                Build.A.Transaction.WithGasPrice(4).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(3).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyD).WithNonce(0).TestObject,
            };

            return Build.A.Block.Genesis.WithTransactions(transactions).TestObject;
        }

        public static Block GetTestBlockB()
        {
            Transaction[] transactions = {
                Build.A.Transaction.WithGasPrice(8).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject,
                Build.A.Transaction.WithGasPrice(7).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(1).TestObject,
                Build.A.Transaction.WithGasPrice(6).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(1).TestObject,
                Build.A.Transaction.WithGasPrice(5).SignedAndResolved(TestItem.PrivateKeyD).WithNonce(1).TestObject,
            };
            Keccak blockAHash = GetTestBlockA().Hash;
            return Build.A.Block.WithNumber(1).WithTransactions(transactions).WithParentHash(blockAHash).TestObject;
        }
    }
}
