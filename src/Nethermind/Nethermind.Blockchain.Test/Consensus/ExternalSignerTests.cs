// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
// ReSharper disable AssignNullToNotNullAttribute

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class ExternalSignerTests
    {
        [Test]
        public async Task Test_external_signing()
        {
            var client = new BasicJsonRpcClient(new Uri("http://localhost:8550"), new EthereumJsonSerializer(), new OneLoggerLogManager(new (new TestLogger())));
            ExternalSigner signer = await ExternalSigner.Create(client);

            var result = signer.Sign(Keccak.Zero);

            Assert.That(result, Is.Not.Empty);
        }

        [Test]
        public async Task Test_listing()
        {
            var client = new BasicJsonRpcClient(new Uri("http://localhost:8550"), new EthereumJsonSerializer(), new OneLoggerLogManager(new(new TestLogger())));
            ExternalSigner signer = await ExternalSigner.Create(client);

        }
    }
}
