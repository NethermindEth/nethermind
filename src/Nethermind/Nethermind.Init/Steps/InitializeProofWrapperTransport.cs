// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;

namespace Nethermind.Init.Steps;

/// <summary>Creates the EIP-8288 eth/73 wrapper transport before the network starts.</summary>
/// <remarks>The transport subscribes to wrapper validation on creation, so it offers wrappers validated before the first eth/73 session.</remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockchain)],
    dependents: [typeof(InitializeNetwork)]
)]
public sealed class InitializeProofWrapperTransport(ProofWrapperTransport transport) : IStep
{
    /// <inheritdoc/>
    public Task Execute(CancellationToken cancellationToken)
    {
        GC.KeepAlive(transport);
        return Task.CompletedTask;
    }
}
