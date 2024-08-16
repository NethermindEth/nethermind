// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.JsonRpc;

namespace Nethermind.BlockValidation.Data;

/// <summary>
/// Represents the result of a block validation.
/// </summary>
public class BlockValidationResult
{

    public static ResultWrapper<BlockValidationResult> Invalid(string error)
    {
        return ResultWrapper<BlockValidationResult>.Success(new BlockValidationResult
        {
            Status = BlockValidationStatus.Invalid,
            ValidationError = error
        });
    }

    public static ResultWrapper<BlockValidationResult> Valid()
    {
        return ResultWrapper<BlockValidationResult>.Success(new BlockValidationResult
        {
            Status = BlockValidationStatus.Valid
        });
    }

    public static ResultWrapper<BlockValidationResult> Error(string error)
    {
        return ResultWrapper<BlockValidationResult>.Fail(error);
    }

    /// <summary>
    /// The status of the validation of the builder submissions
    /// </summary>
    public string Status { get; set; } = BlockValidationStatus.Invalid;

    /// <summary>
    /// Message providing additional details on the validation error if the payload is classified as <see cref="ValidationStatus.Invalid"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ValidationError { get; set; }
}
