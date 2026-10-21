// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Steps;

/// <summary>Starts the writer of the snap-pivot history image: it resumes an image left unfinished by a restart, and
/// writes the one a later sync begins.</summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockTree)])]
public class StartPivotHistoryImage(PivotHistoryImageWriter writer) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        writer.Start();
        return Task.CompletedTask;
    }
}
