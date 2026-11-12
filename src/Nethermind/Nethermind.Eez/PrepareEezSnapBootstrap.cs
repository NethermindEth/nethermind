// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System.Threading;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.Eez;

[RunnerStepDependencies([typeof(ReviewBlockTree)], [typeof(InitializeNetwork)])]
internal sealed class PrepareEezSnapBootstrap(EezSnapBootstrap bootstrap) : IStep
{
    public Task Execute(CancellationToken cancellationToken) => bootstrap.Prepare(cancellationToken);
}
