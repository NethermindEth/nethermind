// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class RemoteSignerTests
    {
        [Test]
        public async Task Sign_SigningHash_CallHasCorrectParameters()
        {
            IJsonRpcClient client = Substitute.For<IJsonRpcClient>();
            client.Post<string[]>("account_list").Returns(Task.FromResult<string[]?>([TestItem.AddressA!.ToString()]));
            Task<string?> postMethod = client.Post<string>("account_signData", "application/clique", Arg.Any<string>(), Keccak.Zero);
            var returnValue = (new byte[65]).ToHexString();
            postMethod.Returns(returnValue);
            RemoteSigner signer = await RemoteSigner.Create(client, 0);

            var result = signer.Sign(Keccak.Zero);

            Assert.That(new Signature(returnValue).Bytes, Is.EqualTo(result.Bytes));
        }
    }
}
