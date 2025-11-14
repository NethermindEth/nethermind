// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class XdcSealerTests
{
    [Test]
    public async Task SealBlock_ShouldSignXdcBlockHeader()
    {
        // Arrange
        var sealer = new XdcSealer(new Signer(0, Build.A.PrivateKey.TestObject, NullLogManager.Instance));
        var block = Build.A.Block.WithHeader(Build.A.XdcBlockHeader().TestObject).TestObject;

        // Act
        var sealedBlock = await sealer.SealBlock(block, CancellationToken.None);
        XdcBlockHeader sealedHeader = (XdcBlockHeader)sealedBlock.Header;

        // Assert
        Assert.That(sealedHeader.Validator, Is.Not.Null);
        Assert.That(sealedHeader.Validator!.Length, Is.EqualTo(Signature.Size));
    }
}
