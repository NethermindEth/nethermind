// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeL1Rules
{
    private static JsonObject BlobRules(GethGenesisConfigJson config, SortedSet<ulong> times, ChainSpec spec, out ulong maximum)
    {
        bool chiado = config.ChainId == BlockchainIds.Chiado;
        BlobParameters cancun = Blob(config, "cancun", chiado ? CancunGnosis.Instance : Cancun.Instance);
        BlobParameters prague = Blob(config, "prague", chiado ? PragueGnosis.Instance : Prague.Instance);
        BlobParameters osaka = Blob(config, "osaka", chiado ? OsakaGnosis.Instance : Osaka.Instance);
        maximum = Math.Max(cancun.Allowance, Math.Max(prague.Allowance, osaka.Allowance));
        List<(ulong At, BlobParameters Value)> scheduled = new(6);
        foreach ((string key, ulong? at) in BlobUpdates(config))
        {
            if (at is not { } timestamp || config.BlobSchedule is null || !config.BlobSchedule.TryGetValue(key, out GethBlobScheduleEntry? entry))
                continue;
            scheduled.Add((timestamp, new BlobParameters(entry.Target, entry.Max, entry.BaseFeeUpdateFraction, Osaka.Instance.MaxBlobsPerTx)));
        }
        scheduled.Sort(static (left, right) => left.At.CompareTo(right.At));
        JsonArray updates = [];
        for (int index = 0; index < scheduled.Count; index++)
        {
            (ulong at, BlobParameters value) = scheduled[index];
            if (index > 0 && at <= scheduled[index - 1].At) throw Invalid("blob updates must strictly increase");
            maximum = Math.Max(maximum, value.Allowance);
            times.Add(at);
            updates.Add(new JsonArray(JsonValue.Create(at), value.Json()));
        }
        spec.Parameters.BlobSchedule = [];
        foreach (ulong timestamp in times)
        {
            if (config.CancunTime is not { } first || timestamp < first) continue;
            BlobParameters current = cancun;
            if (config.PragueTime is { } p && timestamp >= p) current = prague;
            if (config.OsakaTime is { } o && timestamp >= o)
            {
                current = osaka;
                foreach ((ulong at, BlobParameters value) in scheduled)
                {
                    if (at > timestamp) break;
                    current = value;
                }
            }
            spec.Parameters.BlobSchedule.Add(new BlobScheduleSettings
            {
                Timestamp = timestamp,
                Target = current.Target,
                Max = current.Max,
                BaseFeeUpdateFraction = current.Fraction
            });
        }
        return new JsonObject { ["cancun"] = cancun.Json(), ["prague"] = prague.Json(), ["osaka"] = osaka.Json(), ["scheduled"] = updates };
    }

    private static BlobParameters Blob(GethGenesisConfigJson config, string name, IReleaseSpec defaults) =>
        config.BlobSchedule is { } schedule && schedule.TryGetValue(name, out GethBlobScheduleEntry? entry)
            ? new(entry.Target, entry.Max, entry.BaseFeeUpdateFraction, name == "osaka" ? Osaka.Instance.MaxBlobsPerTx : entry.Max)
            : new(defaults.TargetBlobCount, defaults.MaxBlobCount, defaults.BlobBaseFeeUpdateFraction, defaults.MaxBlobsPerTx);

    private static (string Key, ulong? At)[] BlobUpdates(GethGenesisConfigJson c) =>
    [
        ("bpo1", c.Bpo1Time), ("bpo2", c.Bpo2Time), ("bpo3", c.Bpo3Time), ("bpo4", c.Bpo4Time), ("bpo5", c.Bpo5Time),
        ("Amsterdam", c.AmsterdamTime)
    ];

    private readonly record struct BlobParameters(ulong Target, ulong Max, ulong Fraction, ulong MaxPerTransaction)
    {
        internal ulong Allowance => Math.Min(Max, MaxPerTransaction);
        internal JsonObject Json() => new() { ["target"] = Target, ["max"] = Max, ["baseFeeUpdateFraction"] = Fraction };
    }
}
