// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
// ReSharper disable AssignNullToNotNullAttribute

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class RemoteSignerTests
    {
        [Test]
        public async Task Test_external_signing()
        {
            var client = new BasicJsonRpcClient(new Uri("http://localhost:8550"), new EthereumJsonSerializer(), new OneLoggerLogManager(new (new TestLogger())));
            RemoteSigner signer = await RemoteSigner.Create(client, 0);

            var result = signer.Sign(Keccak.Zero);

            Assert.That(result, Is.Not.Empty);

        }

        [Test]
        public async Task Test_Tx_external_signing()
        {
            var client = new BasicJsonRpcClient(new Uri("http://localhost:8550"), new EthereumJsonSerializer(), new OneLoggerLogManager(new(new TestLogger())));
            Transaction tx = Build.A.Transaction.WithSenderAddress(new Address("0xBaB1b2527eDB13AaC27A3982A89A61b8007C5Df1")).WithTo(TestItem.AddressA).WithChainId(10081).TestObject;
            RemoteSigner signer = await RemoteSigner.Create(client, 0);

            await signer.Sign(tx);

            Assert.That(tx.Signature, Is.Not.Null);
            new TxValidator(10081).IsWellFormed(tx, MuirGlacier.Instance);
        }

        [Test]
        public async Task Test_listing()
        {
            var client = new BasicJsonRpcClient(new Uri("http://localhost:8550"), new EthereumJsonSerializer(), new OneLoggerLogManager(new(new TestLogger())));
            RemoteSigner signer = await RemoteSigner.Create(client, 0);

        }
    }
}
