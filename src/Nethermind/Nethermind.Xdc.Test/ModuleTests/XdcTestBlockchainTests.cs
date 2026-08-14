// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class XdcTestBlockchainTests
{
    private XdcTestBlockchain _blockchain;

    [SetUp]
    public async Task Setup() =>
        _blockchain = await XdcTestBlockchain.Create();

    [TearDown]
    public void TearDown() =>
        _blockchain?.Dispose();

    [TestCase(180)]
    [TestCase(91)]
    public async Task SetupXdcChainAndValidateAllHeaders(int count)
    {
        //Shorten the epoch length so we can run the test faster
        _blockchain.ChangeReleaseSpec((c) =>
        {
            c.EpochLength = 90UL;
            c.Gap = 45UL;
        });

        await _blockchain.AddBlocks(count);
        IHeaderValidator headerValidator = _blockchain.Container.Resolve<IHeaderValidator>();
        BlockHeader parent = _blockchain.BlockTree.Genesis!;
        for (ulong i = 1; i < _blockchain.BlockTree.Head!.Number; i++)
        {
            Block? block = _blockchain.BlockTree.FindBlock(i, BlockTreeLookupOptions.None);
            Assert.That(block, Is.Not.Null);
            Assert.That(headerValidator.Validate(block!.Header, parent, false, out string? error), Is.True, "Header validation failed: " + error);
            parent = block.Header;
        }
    }
}
