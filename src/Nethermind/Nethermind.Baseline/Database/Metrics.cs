// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Baseline.Database
{
    public static class Metrics
    {
        [Description("Number of baseline tree DB reads.")]
        public static long BaselineTreeDbReads { get; set; }

        [Description("Number of baseline tree DB writes.")]
        public static long BaselineTreeDbWrites { get; set; }

        [Description("Number of baseline tree metadata DB reads.")]
        public static long BaselineTreeMetadataDbReads { get; set; }

        [Description("Number of baseline tree metadata DB writes.")]
        public static long BaselineTreeMetadataDbWrites { get; set; }
    }
}
