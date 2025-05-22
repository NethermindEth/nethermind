// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Specs.ChainSpecStyle.Json;

public class BlobScheduleSettings : IComparable<BlobScheduleSettings>
{
    public ulong Timestamp { get; set; }

    public ulong Target { get; set; }

    public ulong Max { get; set; }

    public ulong BaseFeeUpdateFraction { get; set; }

    public int CompareTo(BlobScheduleSettings? other) => other is null ? 1 : Timestamp.CompareTo(other.Timestamp);
}
