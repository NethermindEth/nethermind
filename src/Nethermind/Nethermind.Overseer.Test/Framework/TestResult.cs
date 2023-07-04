// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Overseer.Test.Framework
{
    public class TestResult
    {
        public int Order { get; }
        public string Name { get; }
        public bool Passed { get; }

        public TestResult(int order, string name, bool passed)
        {
            Order = order;
            Name = name;
            Passed = passed;
        }
    }
}
