// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.ExternalSigner.Plugin;
using Nethermind.JsonRpc.Client;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class ClefSignerTests
    {
        [Test]
        public void Sign_SigningHash_RequestHasCorrectParameters()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            Task<string?> postMethod = client.Post<string>("account_signData", "text/plain", Arg.Any<string>(), Keccak.Zero);
            var returnValue = (new byte[65]).ToHexString();
            postMethod.Returns(returnValue);
            ClefSigner sut = ClefSigner.Create(new ClefWallet(client));

            var result = sut.Sign(Keccak.Zero);

            Assert.That(new Signature(returnValue).Bytes.SequenceEqual(result.Bytes));
        }

        [Test]
        public async Task Sign_SigningCliqueHeader_PassingCorrectClefParametersForRequest()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            Task<string?> postMethod = client.Post<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
            var returnValue = (new byte[65]).ToHexString();
            postMethod.Returns(returnValue);
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            ClefSigner sut = ClefSigner.Create(new ClefWallet(client));

            sut.Sign(blockHeader);

            await client.Received().Post<string>("account_signData", "application/x-clique-header", Arg.Any<string>(), Arg.Any<string>());
        }


        [TestCase(0, 27)]
        [TestCase(1, 28)]
        public void Sign_RecoveryIdIsSetToCliqueValues_RecoveryIdIsAdjusted(byte recId, byte expected)
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            Task<string?> postMethod = client.Post<string>("account_signData", "application/x-clique-header", Arg.Any<string>(), Arg.Any<string>());
            var returnValue = (new byte[65]);
            returnValue[64] = recId;
            postMethod.Returns(returnValue.ToHexString());
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            ClefSigner sut = ClefSigner.Create(new ClefWallet(client));

            var result = sut.Sign(blockHeader);

            Assert.That(result.V, Is.EqualTo(expected));
        }

        [Test]
        public void Create_SignerAddressSpecified_CorrectAddressIsSet()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString(), TestItem.AddressB!.ToString()]));

            ClefSigner sut = ClefSigner.Create(new ClefWallet(client), TestItem.AddressB);

            Assert.That(sut.Address, Is.EqualTo(TestItem.AddressB));
        }

        [Test]
        public void Create_SignerAddressDoesNotExists_ThrowInvalidOperationException()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString(), TestItem.AddressB!.ToString()]));

            Assert.That(() => ClefSigner.Create(new ClefWallet(client), TestItem.AddressC), Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void SetSigner_TryingToASigner_ThrowInvalidOperationException()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            ClefSigner sut = ClefSigner.Create(new ClefWallet(client));

            Assert.That(() => sut.SetSigner(Build.A.PrivateKey.TestObject), Throws.InstanceOf<InvalidOperationException>());
        }
    }
}
