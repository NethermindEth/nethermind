// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ethereum.Test.Base.Interfaces
{
    public interface IStateTestRunner
    {
        Task<IEnumerable<EthereumTestResult>> RunTests();
    }
}
