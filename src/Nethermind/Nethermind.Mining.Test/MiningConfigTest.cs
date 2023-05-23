// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core.Exceptions;
using NUnit.Framework;

namespace Nethermind.Mining.Test;

[TestFixture]
public class MiningConfigTest
{
    [TestCase]
    [TestCase("")]
    [TestCase("1, 2, 3, 4, 5")]
    [TestCase("Other Extra data")]

    public void Test(string data = "Nethermind")
    {
        IBlocksConfig config = new BlocksConfig();
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        config.ExtraData = data;

        Assert.That(data, Is.EqualTo(config.ExtraData));
        Assert.That(dataBytes, Is.EqualTo(config.GetExtraDataBytes()));
    }

    [Test]
    public void TestTooLongExtraData()
    {
        string data = "1234567890" +
                      "1234567890" +
                      "1234567890" +
                      "1234567890";

        IBlocksConfig config = new BlocksConfig();
        string defaultData = config.ExtraData;
        byte[] defaultDataBytes = Encoding.UTF8.GetBytes(defaultData);

        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        Assert.Greater(dataBytes.Length, 32);

        Assert.Throws<InvalidConfigurationException>(() => config.ExtraData = data); //throw on update
        Assert.That(defaultData, Is.EqualTo(config.ExtraData)); // Keep previous one
        Assert.That(defaultDataBytes, Is.EqualTo(config.GetExtraDataBytes()));
    }
}
