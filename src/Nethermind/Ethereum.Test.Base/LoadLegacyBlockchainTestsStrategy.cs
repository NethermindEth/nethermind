// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

namespace Ethereum.Test.Base;

/// <param name="legacySet">
/// The fixture set under <c>LegacyTests</c> to load from: <see cref="LegacyTestSet.Cancun"/>, or
/// <see cref="LegacyTestSet.Constantinople"/> for the few directories only that older set has.
/// </param>
public class LoadLegacyBlockchainTestsStrategy(string legacySet = LegacyTestSet.Cancun)
    : TestLoadStrategy(Path.Combine("LegacyTests", legacySet, "BlockchainTests"), TestType.Blockchain)
{
    protected override EthereumTest? HandleLoadFailure(string testFile, Exception e) =>
        new BlockchainTest { Name = testFile, LoadFailure = $"Failed to load: {e}" };
}
