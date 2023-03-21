// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.FullPruning;

/// <summary>
/// Status of Full Pruning
/// </summary>
[JsonConverter(typeof(LowerCaseJsonStringEnumConverter))]
public enum PruningStatus
{
    /// <summary>
    /// Default - full pruning is disabled.
    /// </summary>
    Disabled,

    /// <summary>
    /// Delayed - full pruning is temporary disabled. Too little time from previous successful pruning.
    /// </summary>
    Delayed,

    /// <summary>
    /// Full pruning is already in progress.
    /// </summary>
    InProgress,

    /// <summary>
    /// Full pruning was triggered and is starting.
    /// </summary>
    Starting,
}
