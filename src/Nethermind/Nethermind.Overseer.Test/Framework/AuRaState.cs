// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Overseer.Test.Framework
{
    public class AuRaState : ITestState
    {
        public IDictionary<long, (string Author, long Step)> Blocks { get; set; } = new SortedDictionary<long, (string Author, long Step)>();
        public long BlocksCount { get; set; }
    }
}
