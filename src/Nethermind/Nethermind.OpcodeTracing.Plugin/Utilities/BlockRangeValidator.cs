// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin.Utilities;

/// <summary>
/// Validates block range configuration for opcode tracing.
/// </summary>
public static class BlockRangeValidator
{
    /// <summary>
    /// Validates the block range configuration.
    /// </summary>
    /// <param name="config">The opcode tracing configuration.</param>
    /// <param name="currentChainTip">The current chain tip block number.</param>
    /// <returns>A validation result indicating success, warnings, or errors.</returns>
    public static ValidationResult Validate(IOpcodeTracingConfig config, long currentChainTip)
    {
        if (config is null)
        {
            return ValidationResult.Error("Configuration is null");
        }

        // VR-003: At least one range specification required
        if (config.StartBlock is null && config.EndBlock is null && config.Blocks is null)
        {
            return ValidationResult.Error("No block range specified. Provide StartBlock/EndBlock or Blocks parameter.");
        }

        // VR-001: Explicit range validation
        if (config.StartBlock.HasValue && config.EndBlock.HasValue)
        {
            if (config.StartBlock.Value > config.EndBlock.Value)
            {
                return ValidationResult.Error($"Invalid range: StartBlock ({config.StartBlock}) > EndBlock ({config.EndBlock})");
            }

            if (config.StartBlock.Value < 0)
            {
                return ValidationResult.Error("StartBlock must be non-negative");
            }
        }

        // VR-002: Conflicting configuration warning
        if (config.StartBlock.HasValue && config.Blocks.HasValue)
        {
            return ValidationResult.Warning("Both StartBlock and Blocks specified. Using explicit range, ignoring Blocks parameter.");
        }

        if (config.EndBlock.HasValue && config.Blocks.HasValue && !config.StartBlock.HasValue)
        {
            return ValidationResult.Warning("Both EndBlock and Blocks specified. This configuration is ambiguous.");
        }

        // VR-004: Blocks beyond chain tip
        if (config.EndBlock.HasValue && config.EndBlock.Value > currentChainTip)
        {
            return ValidationResult.Error($"EndBlock ({config.EndBlock}) exceeds current chain tip ({currentChainTip})");
        }

        // Validate Blocks parameter
        if (config.Blocks.HasValue && config.Blocks.Value <= 0)
        {
            return ValidationResult.Error("Blocks parameter must be positive");
        }

        return ValidationResult.Success();
    }
}

/// <summary>
/// Represents the result of a validation operation.
/// </summary>
public sealed class ValidationResult
{
    private ValidationResult(ValidationStatus status, string? message)
    {
        Status = status;
        Message = message;
    }

    /// <summary>
    /// Gets the validation status.
    /// </summary>
    public ValidationStatus Status { get; }

    /// <summary>
    /// Gets the validation message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets a value indicating whether the validation was successful.
    /// </summary>
    public bool IsSuccess => Status == ValidationStatus.Success;

    /// <summary>
    /// Gets a value indicating whether the validation resulted in a warning.
    /// </summary>
    public bool IsWarning => Status == ValidationStatus.Warning;

    /// <summary>
    /// Gets a value indicating whether the validation resulted in an error.
    /// </summary>
    public bool IsError => Status == ValidationStatus.Error;

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    /// <returns>A validation result indicating success.</returns>
    public static ValidationResult Success() => new(ValidationStatus.Success, null);

    /// <summary>
    /// Creates a warning validation result.
    /// </summary>
    /// <param name="message">The warning message.</param>
    /// <returns>A validation result indicating a warning.</returns>
    public static ValidationResult Warning(string message) => new(ValidationStatus.Warning, message);

    /// <summary>
    /// Creates an error validation result.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>A validation result indicating an error.</returns>
    public static ValidationResult Error(string message) => new(ValidationStatus.Error, message);
}

/// <summary>
/// Specifies the validation status.
/// </summary>
public enum ValidationStatus
{
    /// <summary>
    /// Validation passed successfully.
    /// </summary>
    Success,

    /// <summary>
    /// Validation passed with warnings.
    /// </summary>
    Warning,

    /// <summary>
    /// Validation failed with errors.
    /// </summary>
    Error
}
