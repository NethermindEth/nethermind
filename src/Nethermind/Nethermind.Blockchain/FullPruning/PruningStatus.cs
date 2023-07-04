// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Nethermind.Blockchain.FullPruning;

/// <summary>
/// Status of Full Pruning
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum PruningStatus
{
    /// <summary>
    /// Default - full pruning is disabled.
    /// </summary>
    Disabled,

    /// <summary>
    /// Pruning failed because of low disk space
    /// </summary>
    NotEnoughDiskSpace,

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
