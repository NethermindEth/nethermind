// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Core;
using Nethermind.Api.Steps;
using Nethermind.Core.Exceptions;

namespace Nethermind.Init.Steps;

/// <summary>Fails startup before the block tree is initialized when the node points at a removed state schema.</summary>
/// <remarks>
/// Resolving the policy is the validation: its constructor throws <see cref="InvalidConfigurationException"/>.
/// It is resolved lazily so the failure surfaces from <see cref="Execute"/> rather than from step construction,
/// and unwrapped from Autofac's <see cref="DependencyResolutionException"/> so the configuration exit code is kept.
/// </remarks>
[RunnerStepDependencies(typeof(ApplyMemoryHint))]
public sealed class ValidateFlatState(Lazy<FlatStateActivationPolicy> policy) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _ = policy.Value;
        }
        catch (DependencyResolutionException e) when (e.GetBaseException() is InvalidConfigurationException invalidConfiguration)
        {
            ExceptionDispatchInfo.Throw(invalidConfiguration);
        }

        return Task.CompletedTask;
    }
}
