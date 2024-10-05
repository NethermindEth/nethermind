// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Shutter;

public class Metrics
{
    [CounterMetric]
    [Description("Number of keys not received.")]
    public static ulong ShutterKeysMissed { get; set; }

    [Description("Eon of the latest block.")]
    public static ulong ShutterEon { get; set; }

    [Description("Size of keyper set in current eon.")]
    public static int ShutterKeypers { get; set; }

    [Description("Number of keypers assumed to be honest and online for current eon.")]
    public static int ShutterThreshold { get; set; }

    [Description("Number of transactions since Shutter genesis.")]
    public static ulong ShutterTxPointer { get; set; }

    [GaugeMetric]
    [Description("Relative time offset (ms) from slot boundary that keys were received.")]
    public static long ShutterKeysReceivedTimeOffset { get; set; }

    [GaugeMetric]
    [Description("Number of transactions included.")]
    public static uint ShutterTransactions { get; set; }

    [GaugeMetric]
    [Description("Number of invalid transactions that could not be included.")]
    public static uint ShutterBadTransactions { get; set; }

    [GaugeMetric]
    [Description("Amount of encrypted gas used.")]
    public static ulong ShutterEncryptedGasUsed { get; set; }
}
