// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;

namespace Ethereum.Test.Base;

/// <param name="legacySet">
/// The fixture set under <c>LegacyTests</c> to load from: <see cref="LegacyTestSet.Cancun"/>, or
/// <see cref="LegacyTestSet.Constantinople"/> for the few directories only that older set has.
/// </param>
public class LoadLegacyGeneralStateTestsStrategy(string legacySet = LegacyTestSet.Cancun)
    : TestLoadStrategy(Path.Combine("LegacyTests", legacySet, "GeneralStateTests"), TestType.State)
{
    protected override void OnTestLoaded(EthereumTest test)
    {
        // Mark legacy tests to use old coinbase behavior for backward compatibility
        if (test is GeneralStateTest gst)
            gst.IsLegacy = true;
    }
}
