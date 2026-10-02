// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Init.Steps;

/// <summary>Fails startup when a configured EIP-8288 chain lacks its required native verifier.</summary>
public sealed class InitializeLeanBackend(ISpecProvider specProvider, ILeanProofVerifier proofVerifier) : IStep
{
    /// <inheritdoc/>
    public Task Execute(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (specProvider.GetFinalSpec().IsEip8288Enabled)
            proofVerifier.EnsureAvailable();
        return Task.CompletedTask;
    }
}
