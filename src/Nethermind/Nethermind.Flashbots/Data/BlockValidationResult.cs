// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.JsonRpc;

namespace Nethermind.Flashbots.Data;

/// <summary>
/// Represents the result of a block validation.
/// </summary>
public class FlashbotsResult
{

    public static ResultWrapper<FlashbotsResult> Invalid(string error)
    {
        return ResultWrapper<FlashbotsResult>.Fail(error, new FlashbotsResult
        {
            Status = FlashbotsStatus.Invalid,
            ValidationError = error
        });
    }

    public static ResultWrapper<FlashbotsResult> Valid()
    {
        return ResultWrapper<FlashbotsResult>.Success(new FlashbotsResult
        {
            Status = FlashbotsStatus.Valid
        });
    }

    public static ResultWrapper<FlashbotsResult> Error(string error)
    {
        return ResultWrapper<FlashbotsResult>.Fail(error);
    }

    /// <summary>
    /// The status of the validation of the builder submissions
    /// </summary>
    public string Status { get; set; } = FlashbotsStatus.Invalid;

    /// <summary>
    /// Message providing additional details on the validation error if the payload is classified as <see cref="ValidationStatus.Invalid"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ValidationError { get; set; }
}
