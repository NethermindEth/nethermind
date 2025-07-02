// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.Db;

/// <summary>
/// Defines pruning mode.
/// </summary>
[Flags]
public enum PruningMode
{
    [Description("No pruning (archive).")]
    /// <summary>
    /// No pruning - full archive.
    /// </summary>
    None = 0,

    [Description("In-memory pruning.")]
    /// <summary>
    /// In memory pruning.
    /// </summary>
    Memory = 1,

    [Description("Full pruning.")]
    /// <summary>
    /// Full pruning.
    /// </summary>
    Full = 2,

    [Description("Combined in-memory and full pruning.")]
    /// <summary>
    /// Both in memory and full pruning.
    /// </summary>
    Hybrid = Memory | Full
}

public static class PruningModeExtensions
{
    public static bool IsMemory(this PruningMode mode) => (mode & PruningMode.Memory) == PruningMode.Memory;
    public static bool IsFull(this PruningMode mode) => (mode & PruningMode.Full) == PruningMode.Full;
}
