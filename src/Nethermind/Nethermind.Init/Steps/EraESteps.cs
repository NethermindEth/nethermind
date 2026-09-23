// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.EraE;

namespace Nethermind.Init.Steps;

[StepCommand("erae-import", "Import blocks and receipts from a directory of EraE archives.")]
[RunnerStepDependencies(typeof(ReviewBlockTree))]
public class EraEImportStep(EraCliRunner eraCliRunner) : IStep
{
    public Task Execute(CancellationToken cancellationToken) => eraCliRunner.Import(cancellationToken);
}

[StepCommand("erae-export", "Export blocks and receipts to a directory of EraE archives.")]
[RunnerStepDependencies(typeof(ReviewBlockTree))]
public class EraEExportStep(EraCliRunner eraCliRunner) : IStep
{
    public Task Execute(CancellationToken cancellationToken) => eraCliRunner.Export(cancellationToken);
}
