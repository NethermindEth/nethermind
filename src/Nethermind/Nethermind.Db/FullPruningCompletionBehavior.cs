// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Db;

/// <summary>
/// Defines what to do when a full prune completes.
/// </summary>
public enum FullPruningCompletionBehavior
{
    [Description("No action.")]
    /// <summary>
    /// Do nothing once pruning is completed.
    /// </summary>
    None,

    [Description("Shuts Nethermind down when pruning succeeds but leaves it running when fails.")]
    /// <summary>
    /// Shut Nethermind down gracefully if pruning was successful, but leave it running if it failed.
    /// </summary>
    ShutdownOnSuccess,

    [Description("Shuts Nethermind down when pruning completes, regardless of its status.")]
    /// <summary>
    /// Shut Nethermind down gracefully when pruning completes, regardless of whether or not it succeeded.
    /// </summary>
    AlwaysShutdown
}
