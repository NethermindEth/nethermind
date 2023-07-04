// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityTraceAddressConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip()
        {
            bool Comparer(int[] a, int[] b)
            {
                if (a.Length != b.Length)
                {
                    return false;
                }

                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            TestRoundtrip(new[] { 1, 2, 3, 1000, 10000 }, Comparer, new ParityTraceAddressConverter());
        }
    }
}
