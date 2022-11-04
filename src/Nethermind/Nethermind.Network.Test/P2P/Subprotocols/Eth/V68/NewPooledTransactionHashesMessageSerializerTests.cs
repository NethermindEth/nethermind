using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V68;

[TestFixture, Parallelizable(ParallelScope.All)]
public class NewPooledTransactionHashesMessageSerializerTests
{
    private static void Test(TxType[] types, int[] sizes, Keccak[] keys)
    {
        NewPooledTransactionHashesMessage68 message = new(types, sizes, keys);
        NewPooledTransactionHashesMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void Roundtrip()
    {
        TxType[] types = { TxType.Legacy, TxType.AccessList, TxType.EIP1559 };
        int[] sizes = { 5, 10, 1500};
        Keccak[] keys = { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
        Test(types, sizes, keys);
    }

    [Test]
    public void Empty_to_string()
    {
        NewPooledTransactionHashesMessage68 message
            = new(new TxType[] {}, new int[] {},new Keccak[] { });
        _ = message.ToString();
    }
}
