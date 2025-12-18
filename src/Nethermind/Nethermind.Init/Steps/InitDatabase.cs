// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Attributes;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    [Todo("Remove. Need to move `InitDatabaseSnapshot` to its own step also")]
    public class InitDatabase : IStep
    {
        public InitDatabase()
        {
        }

        public virtual Task Execute(CancellationToken _)
        {
            return Task.CompletedTask;
        }
    }
}
