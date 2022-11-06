using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V68;

[TestFixture, Parallelizable(ParallelScope.All)]
public class NewPooledTransactionHashesMessageSerializerTests
{
    private static void Test(TxType[] types, int[] sizes, Keccak[] hashes, string expected = null)
    {
        NewPooledTransactionHashesMessage68 message = new(types, sizes, hashes);
        NewPooledTransactionHashesMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, message, expected);
    }

    [Test]
    public void Roundtrip()
    {
        TxType[] types = { TxType.Legacy, TxType.AccessList, TxType.EIP1559 };
        int[] sizes = { 5, 10, 1500 };
        Keccak[] hashes = { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
        Test(types, sizes, hashes);
    }

    [Test]
    public void Empty_serialization()
    {
        TxType[] types = { };
        int[] sizes = { };
        Keccak[] hashes = { };
        Test(types, sizes, hashes, "c3c0c0c0");
    }

    [Test]
    public void Empty_hashes_serialization()
    {
        TxType[] types = { TxType.EIP1559 };
        int[] sizes = { 10 };
        Keccak[] hashes = { };
        Test(types, sizes, hashes, "c5c102c10ac0");
    }

    [Test]
    public void Non_empty_serialization()
    {
        TxType[] types = { TxType.AccessList };
        int[] sizes = { 2 };
        Keccak[] hashes = { TestItem.KeccakA };
        Test(types, sizes, hashes,
            "e6" + "c101" + "c102" + "e1a0"+ TestItem.KeccakA.ToString(false));
    }

    [Test]
    public void Empty_to_string()
    {
        NewPooledTransactionHashesMessage68 message
            = new(new TxType[] { },new int[] { },new Keccak[] { });
        _ = message.ToString();
    }
}
