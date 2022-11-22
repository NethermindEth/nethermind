// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Ethereum.Test.Base.Interfaces
{
    public interface IStateTestRunner
    {
        IEnumerable<EthereumTestResult> RunTests();
    }
}
