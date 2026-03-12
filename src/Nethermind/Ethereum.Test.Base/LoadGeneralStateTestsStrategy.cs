// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base;

public class LoadGeneralStateTestsStrategy() : TestLoadStrategy("GeneralStateTests", TestType.State)
{
    protected override EthereumTest? HandleLoadFailure(string testFile, Exception e) =>
        new GeneralStateTest { Name = testFile, LoadFailure = $"Failed to load: {e}" };
}
