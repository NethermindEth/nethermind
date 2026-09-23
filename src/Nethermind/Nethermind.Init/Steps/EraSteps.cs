// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Era1;

namespace Nethermind.Init.Steps;

[StepCommand("era-import", "Import blocks and receipts from a directory of era1 archives.")]
[RunnerStepDependencies(typeof(ReviewBlockTree))]
public class EraImportStep(EraCliRunner eraCliRunner) : IStep
{
    public Task Execute(CancellationToken cancellationToken) => eraCliRunner.Import(cancellationToken);
}

[StepCommand("era-export", "Export blocks and receipts to a directory of era1 archives.")]
[RunnerStepDependencies(typeof(ReviewBlockTree))]
public class EraExportStep(EraCliRunner eraCliRunner) : IStep
{
    public Task Execute(CancellationToken cancellationToken) => eraCliRunner.Export(cancellationToken);
}
