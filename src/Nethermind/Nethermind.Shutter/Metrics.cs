// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Shutter;

public class Metrics
{
    [CounterMetric]
    [Description("Number of Shutter keys not received.")]
    public static long KeysMissed { get; set; }
}
