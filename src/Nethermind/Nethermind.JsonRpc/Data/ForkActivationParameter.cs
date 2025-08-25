// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Parameter for specifying fork specification overrides for trace operations.
/// Allows tracing blocks with different protocol rules than their original fork.
/// 
/// Examples:
/// - {"forkName": "osaka"} - Use Osaka fork rules
/// - {"activationBlock": 1000000} - Override activation block
/// - {"forkName": "prague", "activationBlock": 0} - Use Prague from genesis
/// </summary>
public class ForkActivationParameter
{
    private static readonly Dictionary<string, Func<IReleaseSpec>> ForkRegistry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["frontier"] = () => Frontier.Instance,
        ["homestead"] = () => Homestead.Instance,
        ["tangerinewhistle"] = () => TangerineWhistle.Instance,
        ["spuriousdragon"] = () => SpuriousDragon.Instance,
        ["byzantium"] = () => Byzantium.Instance,
        ["constantinople"] = () => Constantinople.Instance,
        ["constantinoplefix"] = () => ConstantinopleFix.Instance,
        ["istanbul"] = () => Istanbul.Instance,
        ["muirglacier"] = () => MuirGlacier.Instance,
        ["berlin"] = () => Berlin.Instance,
        ["london"] = () => London.Instance,
        ["arrowglacier"] = () => ArrowGlacier.Instance,
        ["grayglacier"] = () => GrayGlacier.Instance,
        ["paris"] = () => Paris.Instance,
        ["shanghai"] = () => Shanghai.Instance,
        ["cancun"] = () => Cancun.Instance,
        ["prague"] = () => Prague.Instance,
        ["osaka"] = () => Osaka.Instance
    };

    /// <summary>
    /// Name of the fork to use for tracing (e.g., "prague", "osaka")
    /// </summary>
    [JsonPropertyName("forkName")]
    public string? ForkName { get; set; }

    /// <summary>
    /// Block number at which the fork should be considered active (legacy compatibility)
    /// </summary>
    [JsonPropertyName("blockNumber")]
    public long? BlockNumber { get; set; }

    /// <summary>
    /// Timestamp at which the fork should be considered active (legacy compatibility)
    /// </summary>
    [JsonPropertyName("timestamp")]
    public ulong? Timestamp { get; set; }

    /// <summary>
    /// Block number at which the fork should be considered active
    /// </summary>
    [JsonPropertyName("activationBlock")]
    public long? ActivationBlock { get; set; }

    /// <summary>
    /// Timestamp at which the fork should be considered active
    /// </summary>
    [JsonPropertyName("activationTimestamp")]
    public ulong? ActivationTimestamp { get; set; }

    /// <summary>
    /// Gets available fork names for validation
    /// </summary>
    public static IEnumerable<string> AvailableForks => ForkRegistry.Keys;

    /// <summary>
    /// Legacy method for backward compatibility
    /// </summary>
    /// <returns>ForkActivation instance</returns>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public ForkActivation ToForkActivation()
    {
        var result = TryResolve(null);
        if (!result.IsSuccess)
            throw new ArgumentException(result.ErrorMessage);
        return result.Activation;
    }

    /// <summary>
    /// Attempts to resolve the fork specification and activation
    /// </summary>
    /// <param name="specProvider">Current spec provider for fallback resolution</param>
    /// <returns>Result containing the resolved spec and activation, or error details</returns>
    public ForkResolutionResult TryResolve(ISpecProvider? specProvider)
    {
        // Support legacy parameters
        var effectiveActivationBlock = ActivationBlock ?? BlockNumber;
        var effectiveActivationTimestamp = ActivationTimestamp ?? Timestamp;

        // Validate input
        if (string.IsNullOrEmpty(ForkName) && !effectiveActivationBlock.HasValue && !effectiveActivationTimestamp.HasValue)
        {
            return ForkResolutionResult.Failure("Fork specification must provide forkName, activationBlock, activationTimestamp, or combination");
        }

        if (effectiveActivationBlock.HasValue && effectiveActivationBlock.Value < 0)
        {
            return ForkResolutionResult.Failure($"Activation block {effectiveActivationBlock} must be non-negative");
        }

        try
        {
            IReleaseSpec? resolvedSpec = null;
            ForkActivation activation;

            if (!string.IsNullOrEmpty(ForkName))
            {
                if (!ForkRegistry.TryGetValue(ForkName, out var forkFactory))
                {
                    var availableForks = string.Join(", ", AvailableForks);
                    return ForkResolutionResult.Failure($"Unknown fork '{ForkName}'. Available forks: {availableForks}");
                }
                resolvedSpec = forkFactory();
            }

            activation = CreateForkActivation(effectiveActivationBlock, effectiveActivationTimestamp);

            if (resolvedSpec == null && specProvider != null)
            {
                resolvedSpec = specProvider.GetSpec(activation);
            }

            return ForkResolutionResult.Success(resolvedSpec, activation);
        }
        catch (Exception ex)
        {
            return ForkResolutionResult.Failure($"Failed to resolve fork specification: {ex.Message}");
        }
    }

    private ForkActivation CreateForkActivation(long? effectiveActivationBlock, ulong? effectiveActivationTimestamp)
    {
        if (effectiveActivationBlock.HasValue && effectiveActivationTimestamp.HasValue)
        {
            return new ForkActivation(effectiveActivationBlock.Value, effectiveActivationTimestamp.Value);
        }
        
        if (effectiveActivationBlock.HasValue)
        {
            return new ForkActivation(effectiveActivationBlock.Value);
        }
        
        if (effectiveActivationTimestamp.HasValue)
        {
            return ForkActivation.TimestampOnly(effectiveActivationTimestamp.Value);
        }

        // Default to genesis
        return new ForkActivation(0);
    }
}

/// <summary>
/// Result of fork resolution containing either success or error information
/// </summary>
public class ForkResolutionResult
{
    public bool IsSuccess { get; }
    public IReleaseSpec? ResolvedSpec { get; }
    public ForkActivation Activation { get; }
    public string? ErrorMessage { get; }

    private ForkResolutionResult(bool isSuccess, IReleaseSpec? resolvedSpec, ForkActivation activation, string? errorMessage)
    {
        IsSuccess = isSuccess;
        ResolvedSpec = resolvedSpec;
        Activation = activation;
        ErrorMessage = errorMessage;
    }

    public static ForkResolutionResult Success(IReleaseSpec? spec, ForkActivation activation) =>
        new(true, spec, activation, null);

    public static ForkResolutionResult Failure(string errorMessage) =>
        new(false, null, default, errorMessage);
}