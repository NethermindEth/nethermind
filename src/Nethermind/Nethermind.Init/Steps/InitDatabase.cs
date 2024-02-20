// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public class InitDatabase : IStep
    {
        public virtual Task Execute(CancellationToken _)
        {
            // Init database snapshot uses this
            return Task.CompletedTask;
        }
    }
}
