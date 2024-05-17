// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(MigrateConfigs))]
    public sealed class ApplyMemoryHint : IStep
    {
        private readonly MemoryHintMan _memoryHintMan;

        public ApplyMemoryHint(MemoryHintMan memoryHintMan)
        {
            _memoryHintMan = memoryHintMan;
        }

        public Task Execute(CancellationToken _)
        {
            uint cpuCount = (uint)Environment.ProcessorCount;
            _memoryHintMan.SetMemoryAllowances(cpuCount);
            return Task.CompletedTask;
        }
    }
}
