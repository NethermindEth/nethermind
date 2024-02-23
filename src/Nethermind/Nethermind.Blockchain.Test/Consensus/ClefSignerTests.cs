// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle.JavaScript;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class ClefSignerTests
    {
        [Test]
        public async Task Sign_SigningHash_RequestHasCorrectParameters()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            Task<string?> postMethod = client.Post<string>("account_signData", "text/plain", Arg.Any<string>(), Keccak.Zero);
            var returnValue = (new byte[65]).ToHexString();
            postMethod.Returns(returnValue);
            ClefSigner sut = await ClefSigner.Create(client, 0);

            var result = sut.Sign(Keccak.Zero);

            Assert.That(new Signature(returnValue).Bytes, Is.EqualTo(result.Bytes));
        }

        [Test]
        public async Task SignCliqueHeader_SigningCliqueHeader_RequestHasCorrectParameters()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            Task<string?> postMethod = client.Post<string>("account_signData", "account/x-clique-header", Arg.Any<string>(), Keccak.Zero);
            var returnValue = (new byte[65]).ToHexString();
            postMethod.Returns(returnValue);
            ClefSigner sut = await ClefSigner.Create(client, 0);

            var result = sut.SignCliqueHeader(new byte[1]);

            Assert.That(new Signature(returnValue).Bytes, Is.EqualTo(result.Bytes));
        }


        [TestCase(0, 27)]
        [TestCase(1, 28)]
        public async Task SignCliqueHeader_RecoveryIdIsSetToCliqueValues_RecoveryIdIsAdjusted(byte recId, byte expected)
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            Task<string?> postMethod = client.Post<string>("account_signData", "account/x-clique-header", Arg.Any<string>(), Keccak.Zero);
            var returnValue = (new byte[65]);
            returnValue[64] = recId;
            postMethod.Returns(returnValue.ToHexString());
            ClefSigner sut = await ClefSigner.Create(client, 0);

            var result = sut.SignCliqueHeader(new byte[1]);

            Assert.That(new Signature(returnValue).V, Is.EqualTo(expected));
        }

        [Test]
        public async Task Create_SignerAddressSpecified_CorrectAddressIsSet()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString(), TestItem.AddressB!.ToString()]));
            
            ClefSigner sut = await ClefSigner.Create(client, 0, TestItem.AddressB);

            Assert.That(sut.Address, Is.EqualTo(TestItem.AddressB));
        }

        [Test]
        public async Task Create_SignerAddressDoesNotExists_Throw()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString(), TestItem.AddressB!.ToString()]));

            ClefSigner sut = await ClefSigner.Create(client, 0, TestItem.AddressC);

            Assert.That(()=> ClefSigner.Create(client, 0, TestItem.AddressC), Throws.InstanceOf<InvalidOperationException>());
        }
    }
}
