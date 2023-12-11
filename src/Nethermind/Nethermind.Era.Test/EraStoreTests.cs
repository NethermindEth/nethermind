// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Era1.Test;
internal class EraStoreTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(1024)]
    [TestCase(8192)]
    [TestCase(10000)]
    [TestCase(8192 + 8192 - 1)]
    public async Task FindBlock_FileContainsBlock_BlockIsReturned(long number)
    {
        var eraFiles = EraReader.GetAllEraFiles("geth", "mainnet").ToArray();
        Assert.That(eraFiles.Count(), Is.GreaterThan(0));
        EraStore sut = new (eraFiles, new FileSystem());
        Block? result = await sut.FindBlock(number);

        Assert.That(result, Is.Not.Null);
    }

    [TestCase(8192 + 8192)]
    [TestCase(8192 + 8192 + 1)]
    [TestCase(8192 + 8192 + 8192)]
    public async Task FindBlock_FileNotContainedInBlock_NullIsReturned(long number)
    {
        var eraFiles = EraReader.GetAllEraFiles("geth", "mainnet").ToArray();
        Assert.That(eraFiles.Count(), Is.GreaterThan(0));
        EraStore sut = new(eraFiles, new FileSystem());
        Block? result = await sut.FindBlock(number);

        Assert.That(result, Is.Null);
    }
}
