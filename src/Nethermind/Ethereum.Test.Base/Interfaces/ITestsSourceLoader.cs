// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Ethereum.Test.Base.Interfaces
{
    public interface ITestSourceLoader
    {
        IEnumerable<EthereumTest> LoadTests();
        IEnumerable<TTestType> LoadTests<TTestType>()
            where TTestType : EthereumTest;
    }
}
