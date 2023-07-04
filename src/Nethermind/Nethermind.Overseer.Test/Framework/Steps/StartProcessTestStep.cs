// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Overseer.Test.Framework.Steps
{
    public class StartProcessTestStep : TestStepBase
    {
        private readonly NethermindProcessWrapper _process;
        private readonly int _delay;

        public StartProcessTestStep(string name, NethermindProcessWrapper process,
            int delay = 0) : base(name)
        {
            _process = process;
            _delay = delay;
        }

        public override async Task<TestResult> ExecuteAsync()
        {
            _process.Start();
            if (_delay > 0)
            {
                await Task.Delay(_delay);
            }

            return GetResult(true);
        }
    }
}
