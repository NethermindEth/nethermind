// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Crypto;
using NUnit.Framework;
using TextEncoding = System.Text.Encoding;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class RipemdTests
    {
        public const string RipemdOfEmptyString = "0000000000000000000000009c1185a5c5e9fc54612808977ee8f548b2258d31";
        public const string RipemdOfAbcString = "0000000000000000000000008eb208f7e05d987a9b044a8e98c6b087f15a0bfc";
        public const string RipemdOfParallelInput = "000000000000000000000000d63c6ba86ccfa690d29fcdf85e6409c806b03671";

        [Test]
        public void Empty_byte_array()
        {
            string result = Ripemd.ComputeString([]);
            Assert.That(result, Is.EqualTo(RipemdOfEmptyString));
        }

        [Test]
        public void ComputeString_DifferentInputs_DoNotLeakState_BetweenCalls()
        {
            byte[] firstInput = TextEncoding.ASCII.GetBytes("abc");
            byte[] secondInput = [];

            for (int i = 0; i < 1000; i++)
            {
                string firstResult = Ripemd.ComputeString(firstInput);
                string secondResult = Ripemd.ComputeString(secondInput);

                Assert.That(firstResult, Is.EqualTo(RipemdOfAbcString));
                Assert.That(secondResult, Is.EqualTo(RipemdOfEmptyString));
            }
        }

        [Test]
        public async Task ComputeString_IsDeterministic_AcrossParallelCalls()
        {
            byte[] input = TextEncoding.ASCII.GetBytes("parallel-input");
            Task[] tasks = new Task[Math.Max(Environment.ProcessorCount, 4)];

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 500; j++)
                    {
                        string result = Ripemd.ComputeString(input);
                        Assert.That(result, Is.EqualTo(RipemdOfParallelInput));
                    }
                });
            }

            await Task.WhenAll(tasks);
        }
    }
}
