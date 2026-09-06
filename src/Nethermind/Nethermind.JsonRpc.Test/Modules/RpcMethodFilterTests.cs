// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class RpcMethodFilterTests
    {
        private const string FilePath = "path";

        [TestCase("eth_.*", "eth_blocknumber", true)]
        [TestCase("eth_.*", "debug_blocknumber", false)]
        [TestCase("parity_.*", "parity_trace", true)]
        public void Test(string regex, string methodName, bool expectedResult)
        {
            IFileSystem fileSystemSub = Substitute.For<IFileSystem>();
            fileSystemSub.File.Exists(FilePath).Returns(true);
            fileSystemSub.File.ReadLines(FilePath).Returns(new[] { regex });

            RpcMethodFilter filter = new(FilePath, fileSystemSub, LimboLogs.Instance.GetClassLogger<RpcMethodFilterTests>());
            Assert.That(filter.AcceptMethod(methodName), Is.EqualTo(expectedResult));
        }

        [Test]
        public void Test_multiple_lines()
        {
            IFileSystem fileSystemSub = Substitute.For<IFileSystem>();
            fileSystemSub.File.Exists(FilePath).Returns(true);
            fileSystemSub.File.ReadLines(FilePath).Returns(new[] { "eth*", "debug*" });

            RpcMethodFilter filter = new(FilePath, fileSystemSub, LimboLogs.Instance.GetClassLogger<RpcMethodFilterTests>());
            Assert.That(filter.AcceptMethod("eth_blockNumber"), Is.True);
            Assert.That(filter.AcceptMethod("debug_trace"), Is.True);
        }

        [TestCase("eth_blocknumber", "eth_blockNumber", true)]
        [TestCase("eth_blockNumber", "eth_blockNumber", true)]
        [TestCase("ETH_BLOCKNUMBER", "eth_blockNumber", true)]
        public void Test_casing(string regex, string method, bool expectedResult)
        {
            IFileSystem fileSystemSub = Substitute.For<IFileSystem>();
            fileSystemSub.File.Exists(FilePath).Returns(true);
            fileSystemSub.File.ReadLines(FilePath).Returns(new[] { regex });

            RpcMethodFilter filter = new(FilePath, fileSystemSub, LimboLogs.Instance.GetClassLogger<RpcMethodFilterTests>());
            Assert.That(filter.AcceptMethod(method), Is.EqualTo(expectedResult));
        }
    }
}
