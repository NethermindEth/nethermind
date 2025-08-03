// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.JsonRpc.Data;

public class ForkActivationParameter
{
    public string? ForkName { get; set; }
    public long? BlockNumber { get; set; }
    public ulong? Timestamp { get; set; }
    public long? ActivationBlock { get; set; }
    public ulong? ActivationTimestamp { get; set; }
    public bool TryToForkActivation(out ForkActivation activation)
    {
        (bool isSuccess, _, ForkActivation forkActivation, _) = TryResolve(null);
        activation = isSuccess ? forkActivation : default;
        return isSuccess;
    }

    public (bool IsSuccess, IReleaseSpec? ResolvedSpec, ForkActivation Activation, string? ErrorMessage) TryResolve(ISpecProvider? specProvider)
    {
        // Support legacy parameters
        long? effectiveActivationBlock = ActivationBlock ?? BlockNumber;
        ulong? effectiveActivationTimestamp = ActivationTimestamp ?? Timestamp;

        // Validate input
        if (string.IsNullOrEmpty(ForkName) && effectiveActivationBlock is null && effectiveActivationTimestamp is null)
        {
            return (false, null, default, "Fork specification must provide forkName, activationBlock, activationTimestamp, or combination");
        }

        if (effectiveActivationBlock < 0)
        {
            return (false, null, default, $"Activation block {effectiveActivationBlock} must be non-negative");
        }

        try
        {
            IReleaseSpec? resolvedSpec = null;
            ForkActivation activation;

            if (!string.IsNullOrEmpty(ForkName))
            {
                if (specProvider is IForkAwareSpecProvider forkAware && forkAware.TryGetForkSpec(ForkName, out IReleaseSpec? forkSpec))
                {
                    resolvedSpec = forkSpec;
                }
                else
                {
                    IEnumerable<string> availableForks = specProvider is IForkAwareSpecProvider forkAware2 ? forkAware2.AvailableForks : [];
                    string forksText = availableForks.Any() ? string.Join(", ", availableForks) : "none (spec provider doesn't support fork resolution)";
                    return (false, null, default, $"Unknown fork '{ForkName}'. Available forks: {forksText}");
                }
            }

            activation = CreateForkActivation(effectiveActivationBlock, effectiveActivationTimestamp);

            if (resolvedSpec is null && specProvider is not null)
            {
                resolvedSpec = specProvider.GetSpec(activation);
            }

            return (true, resolvedSpec, activation, null);
        }
        catch (ArgumentException ex)
        {
            return (false, null, default, $"Invalid fork activation parameters: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return (false, null, default, $"Fork resolution failed due to invalid state: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, default, $"Unexpected error during fork resolution: {ex.Message}");
        }
    }

    private ForkActivation CreateForkActivation(long? effectiveActivationBlock, ulong? effectiveActivationTimestamp)
    {
        return effectiveActivationBlock.HasValue ? new ForkActivation(effectiveActivationBlock.Value, effectiveActivationTimestamp)
          : effectiveActivationTimestamp.HasValue ? ForkActivation.TimestampOnly(effectiveActivationTimestamp.Value)
          : new ForkActivation(0);
    }
}
