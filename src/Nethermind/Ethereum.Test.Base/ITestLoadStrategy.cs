// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Ethereum.Test.Base
{
    public interface ITestLoadStrategy
    {
        IEnumerable<EthereumTest> Load(string testDirectoryName, string? wildcard = null);
    }
}
