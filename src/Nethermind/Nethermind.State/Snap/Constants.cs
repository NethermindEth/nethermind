// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Snap
{
    public class Constants
    {
        /// <summary>
        /// Maximum distance from head for pivot block validation in merge/beacon chain sync.
        /// Note: For snap serving depth configuration, use ISyncConfig.SnapServingMaxDepth instead.
        /// </summary>
        public const int MaxDistanceFromHead = 128;
    }
}
