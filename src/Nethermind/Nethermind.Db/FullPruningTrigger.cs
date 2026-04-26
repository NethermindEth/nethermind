// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Db;

/// <summary>
/// Triggers for Full Pruning.
/// </summary>
public enum FullPruningTrigger
{
    [Description("Does not trigger. Pruning can be triggered manually.")]
    /// <summary>
    /// Only Manual trigger is supported.
    /// </summary>
    Manual,

    [Description("Triggers when the state DB size is above the specified threshold.")]
    /// <summary>
    /// Automatically triggers on State DB size.
    /// </summary>
    StateDbSize,

    [Description("Triggers when the free disk space where the state DB is stored is below the specified threshold.")]
    /// <summary>
    /// Automatically triggers on Volume free space on volume with State DB.
    /// </summary>
    VolumeFreeSpace
}
