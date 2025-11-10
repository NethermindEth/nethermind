// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class XdcTestBlockchainTests
{
    private XdcTestBlockchain _blockchain;

    [SetUp]
    public async Task Setup()
    {
        _blockchain = await XdcTestBlockchain.Create();
    }

    [TearDown]
    public void TearDown()
    {
        _blockchain?.Dispose();
    }

    [Test]
    public async Task SetupXdcChainAndValidateAllHeaders()
    {
        await _blockchain.AddBlocks(1800);
        IHeaderValidator headerValidator = _blockchain.Container.Resolve<IHeaderValidator>();
        BlockHeader parent = _blockchain.BlockTree.Genesis!;
        for (int i = 1; i < _blockchain.BlockTree.Head!.Number; i++)
        {
            var block = _blockchain.BlockTree.FindBlock(i);
            Assert.That(block, Is.Not.Null);
            string? error;
            Assert.That(headerValidator.Validate(block!.Header, parent, false, out error), Is.True, "Header validation failed: " + error);
            parent = block.Header;
        }
    }
}
