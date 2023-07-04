// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Overseer.Test.Framework.Steps
{
    public abstract class TestStepBase
    {
        public string Name { get; }

        private static int _order = 1;

        protected TestStepBase(string name)
        {
            Name = name;
        }

        public abstract Task<TestResult> ExecuteAsync();

        protected TestResult GetResult(bool passed) => new TestResult(_order++, Name, passed);
    }
}
