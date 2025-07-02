// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;

using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [TestFixture]
    public class ParityAccountChangeConverterTests
    {
        [SetUp]
        public void SetUp()
        {
        }

        [Test]
        public void Does_not_throw_on_change_when_code_after_is_null()
        {
            ParityAccountStateChange change = new()
            {
                Code = new ParityStateChange<byte[]>(new byte[] { 1 }, null!)
            };

            Assert.DoesNotThrow(() => JsonSerializer.Serialize(change, EthereumJsonSerializer.JsonOptions));
        }

        [Test]
        public void Does_not_throw_on_change_when_code_before_is_null()
        {
            ParityAccountStateChange change = new()
            {
                Code = new ParityStateChange<byte[]>(null!, new byte[] { 1 })
            };

            Assert.DoesNotThrow(() => JsonSerializer.Serialize(change, EthereumJsonSerializer.JsonOptions));
        }

        [Test]
        public void Does_not_throw_on_change_storage()
        {
            ParityAccountStateChange change = new()
            {
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>
                {
                    {1, new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {0})}
                }
            };

            Assert.DoesNotThrow(() => JsonSerializer.Serialize(change, EthereumJsonSerializer.JsonOptions));
        }
    }
}
