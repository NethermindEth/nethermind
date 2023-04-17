// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    /// <summary>
    /// Triggers for Full Pruning.
    /// </summary>
    public enum FullPruningTrigger
    {
        /// <summary>
        /// Only Manual trigger is supported.
        /// </summary>
        Manual,

        /// <summary>
        /// Automatically triggers on State DB size.
        /// </summary>
        StateDbSize,

        /// <summary>
        /// Automatically triggers on Volume free space on volume with State DB.
        /// </summary>
        VolumeFreeSpace
    }
}
