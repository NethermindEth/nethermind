// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Overseer.Test.Framework.Steps
{
    public class WaitTestStep : TestStepBase
    {
        private readonly int _delay;

        public WaitTestStep(string name, int delay = 5000) : base(name)
        {
            _delay = delay;
        }

        public override async Task<TestResult> ExecuteAsync()
        {
            if (_delay > 0)
            {
                await Task.Delay(_delay);
            }

            return GetResult(true);
        }
    }
}
