// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class EthereumPrecompileProviderTests
{
    [Test]
    public void GetPrecompiles_ReturnsCachedDictionary()
    {
        EthereumPrecompileProvider provider = new();
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = provider.GetPrecompiles();
        FrozenDictionary<AddressAsKey, CodeInfo> precompilesAgain = provider.GetPrecompiles();

        Assert.That(precompilesAgain, Is.SameAs(precompiles));
    }
}
