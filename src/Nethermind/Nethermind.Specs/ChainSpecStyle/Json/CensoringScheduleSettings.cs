// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Specs.ChainSpecStyle.Json;

public record class CensoringScheduleSettings : IComparable<CensoringScheduleSettings>
{
    public ulong Timestamp { get; set; }

    public Address[] Addresses;

    public int CompareTo(CensoringScheduleSettings? other) => other is null ? 1 : Timestamp.CompareTo(other.Timestamp);
}
