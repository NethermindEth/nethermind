// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    /// <summary>
    /// Defines pruning mode.
    /// </summary>
    [Flags]
    public enum PruningMode
    {
        /// <summary>
        /// No pruning - full archive.
        /// </summary>
        None = 0,

        /// <summary>
        /// In memory pruning.
        /// </summary>
        Memory = 1,

        /// <summary>
        /// Full pruning.
        /// </summary>
        Full = 2,

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
}
