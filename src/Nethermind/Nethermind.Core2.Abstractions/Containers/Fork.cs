// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public struct Fork
    {
        public static Fork Zero = new Fork(ForkVersion.Zero, ForkVersion.Zero, Epoch.Zero);

        public Fork(ForkVersion previousVersion, ForkVersion currentVersion, Epoch epoch)
        {
            PreviousVersion = previousVersion;
            CurrentVersion = currentVersion;
            Epoch = epoch;
        }

        public ForkVersion CurrentVersion { get; }

        /// <summary>
        ///     Gets the epoch of the latest fork
        /// </summary>
        public Epoch Epoch { get; }

        public ForkVersion PreviousVersion { get; }

        public override string ToString()
        {
            return $"{PreviousVersion}_{CurrentVersion}_{Epoch}";
        }
    }
}
