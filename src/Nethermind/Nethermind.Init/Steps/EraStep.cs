// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Era1;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeBlockchain), typeof(LoadGenesisBlock))]
[RunnerStepDependents(typeof(InitializeNetwork))]
public class EraStep : IStep
{
    private readonly EraCliRunner _eraCliRunner;

    public EraStep(EraCliRunner eraCliRunner)
    {
        _eraCliRunner = eraCliRunner;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        await _eraCliRunner.Run(cancellationToken);
    }
}
