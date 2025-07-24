// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Era1;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(
    dependencies: [typeof(ReviewBlockTree)],
    dependents: [typeof(InitializeNetwork)]
)]
public class EraStep(EraCliRunner eraCliRunner) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        await eraCliRunner.Run(cancellationToken);
    }
}
