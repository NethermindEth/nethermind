using System.Text;
using Nethermind.Consensus;
using NUnit.Framework;

namespace Nethermind.Mining.Test;

[TestFixture]
public class MiningConfigTest
{
    [TestCase]
    [TestCase("1, 2, 3, 4, 5")]
    [TestCase("Other Extra data")]
    public void Test(string data = "Nethermind")
    {
        IMiningConfig config = new MiningConfig();
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        config.ExtraData = data;

        Assert.AreEqual(config.ExtraData, data);
        Assert.AreEqual(config.GetExtraDataBytes(), dataBytes);
    }
}
