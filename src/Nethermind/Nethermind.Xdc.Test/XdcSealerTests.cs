// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class XdcSealerTests
{
    [Test]
    public async Task SealBlock_ShouldSignXdcBlockHeader()
    {
        // Arrange
        XdcSealer sealer = new(new Signer(0, Build.A.PrivateKey.TestObject, NullLogManager.Instance), new XdcHeaderDecoder(), NullLogManager.Instance);
        Block block = Build.A.Block.WithHeader(Build.A.XdcBlockHeader().TestObject).TestObject;

        // Act
        Block sealedBlock = await sealer.SealBlock(block, CancellationToken.None);
        XdcBlockHeader sealedHeader = (XdcBlockHeader)sealedBlock.Header;

        // Assert
        Assert.That(sealedHeader.Validator, Is.Not.Null);
        Assert.That(sealedHeader.Validator!.Length, Is.EqualTo(Signature.Size));
    }

    [Test]
    public async Task SealBlock_WhenSignerHasNoKey_ReturnsNull()
    {
        // Signer with a null key cannot sign — sealer should skip rather than throw,
        // matching the existing !CanSeal null-seal path that BlockProducerBase handles.
        XdcSealer sealer = new(new Signer(0, (PrivateKey?)null, NullLogManager.Instance), new XdcHeaderDecoder(), NullLogManager.Instance);
        Block block = Build.A.Block.WithHeader(Build.A.XdcBlockHeader().TestObject).TestObject;

        Block? result = await sealer.SealBlock(block, CancellationToken.None);

        Assert.That(result, Is.Null, "Sealer should return null when the signer cannot produce a signature.");
    }
}
