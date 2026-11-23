// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Test;
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

        [TestCase("^eth_", true, "will be accepted by the JSON RPC filter because of ^eth_.")]
        [TestCase("^debug_", false, "will not be accepted by the JSON RPC filter (no match).")]
        public void Debug_log_encodes_the_method_name(string regex, bool expectedResult, string message)
        {
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Exists(FilePath).Returns(true);
            fileSystem.File.ReadLines(FilePath).Returns(new[] { regex });
            TestLogger logger = new();
            RpcMethodFilter filter = new(FilePath, fileSystem, new(logger));

            bool accepted = filter.AcceptMethod("eth_example\u2028method");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(accepted, Is.EqualTo(expectedResult));
                Assert.That(logger.LogList, Is.EqualTo(new[] { @"eth_example\u2028method " + message }));
            }
        }

        [Test]
        public void Test_multiple_lines()
        {
            IFileSystem fileSystemSub = Substitute.For<IFileSystem>();
            fileSystemSub.File.Exists(FilePath).Returns(true);
            fileSystemSub.File.ReadLines(FilePath).Returns(new[] { "eth*", "debug*" });

            RpcMethodFilter filter = new(FilePath, fileSystemSub, LimboLogs.Instance.GetClassLogger<RpcMethodFilterTests>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(filter.AcceptMethod("eth_blockNumber"), Is.True);
                Assert.That(filter.AcceptMethod("debug_trace"), Is.True);
            }
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
