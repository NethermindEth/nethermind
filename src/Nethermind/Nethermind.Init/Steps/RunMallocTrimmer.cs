// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(MigrateConfigs))]
public class RunMallocTrimmer : IStep
{
    private readonly MallocTrimmer? _mallocTrimmer;

    public RunMallocTrimmer(INethermindApi api)
    {
        IInitConfig initConfig = api.Config<IInitConfig>();
        if (initConfig.MallocTrimmerIntervalSec != 0)
        {
            _mallocTrimmer = new MallocTrimmer(TimeSpan.FromSeconds(initConfig.MallocTrimmerIntervalSec), api.LogManager);
        }
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        return _mallocTrimmer == null ? Task.CompletedTask : _mallocTrimmer!.Run(cancellationToken);
    }
}
