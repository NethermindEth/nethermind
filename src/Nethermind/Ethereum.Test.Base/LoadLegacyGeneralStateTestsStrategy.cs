// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;

namespace Ethereum.Test.Base;

public class LoadLegacyGeneralStateTestsStrategy()
    : TestLoadStrategy(Path.Combine("LegacyTests", "Cancun", "GeneralStateTests"), TestType.State)
{
    protected override void OnTestLoaded(EthereumTest test)
    {
        // Mark legacy tests to use old coinbase behavior for backward compatibility
        if (test is GeneralStateTest gst)
            gst.IsLegacy = true;
    }
}
