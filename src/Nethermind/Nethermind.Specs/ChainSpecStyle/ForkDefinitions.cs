// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

public static class ForkDefinitions
{
    public static readonly Dictionary<string, ForkEipSet> TimestampBasedForks = new()
    {
        ["shanghai"] = new ForkEipSet("Shanghai", [
            nameof(ChainSpecParamsJson.Eip3651TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip3855TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip3860TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip4895TransitionTimestamp)
        ]),
        ["cancun"] = new ForkEipSet("Cancun", [
            nameof(ChainSpecParamsJson.Eip1153TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip4788TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip4844TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip5656TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip6780TransitionTimestamp)
        ]),
        ["dencun"] = new ForkEipSet("Dencun", [
            nameof(ChainSpecParamsJson.Eip1153TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip4788TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip4844TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip5656TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip6780TransitionTimestamp)
        ]),
        ["prague"] = new ForkEipSet("Prague", [
            nameof(ChainSpecParamsJson.Eip2537TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip2935TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip6110TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7002TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7251TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7623TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7702TransitionTimestamp)
        ]),
        ["osaka"] = new ForkEipSet("Osaka", [
            nameof(ChainSpecParamsJson.Eip7594TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7823TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7825TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7883TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7918TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7934TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7939TransitionTimestamp),
            nameof(ChainSpecParamsJson.Eip7951TransitionTimestamp)
        ])
    };

    public static readonly Dictionary<string, ForkEipSet> BlockBasedForks = new()
    {
        ["berlin"] = new ForkEipSet("Berlin", [
            nameof(ChainSpecParamsJson.Eip2565Transition),
            nameof(ChainSpecParamsJson.Eip2929Transition),
            nameof(ChainSpecParamsJson.Eip2930Transition)
        ]),
        ["london"] = new ForkEipSet("London", [
            nameof(ChainSpecParamsJson.Eip1559Transition),
            nameof(ChainSpecParamsJson.Eip3198Transition),
            nameof(ChainSpecParamsJson.Eip3529Transition),
            nameof(ChainSpecParamsJson.Eip3541Transition)
        ])
    };

    public record ForkEipSet(string Name, string[] EipProperties);

    /// <summary>
    /// Chronological order of forks for validation
    /// </summary>
    public static readonly string[] ForkOrder = { "berlin", "london", "shanghai", "cancun", "dencun", "prague", "osaka" };

    /// <summary>
    /// Gets EIP timestamps for a specific fork from parameters
    /// </summary>
    public static Dictionary<string, ulong> GetEipTimestamps(ChainSpecParamsJson parameters, string[] eipProperties)
    {
        var timestamps = new Dictionary<string, ulong>();
        var paramType = typeof(ChainSpecParamsJson);

        foreach (string eipProp in eipProperties)
        {
            var property = paramType.GetProperty(eipProp);
            var value = property?.GetValue(parameters);

            if (value is ulong timestampValue)
                timestamps[eipProp] = timestampValue;
        }
        return timestamps;
    }

    /// <summary>
    /// Gets EIP block numbers for a specific fork from parameters
    /// </summary>
    public static Dictionary<string, long> GetEipBlockNumbers(ChainSpecParamsJson parameters, string[] eipProperties)
    {
        var blockNumbers = new Dictionary<string, long>();
        var paramType = typeof(ChainSpecParamsJson);

        foreach (string eipProp in eipProperties)
        {
            var property = paramType.GetProperty(eipProp);
            var value = property?.GetValue(parameters);

            if (value is long blockValue)
                blockNumbers[eipProp] = blockValue;
        }
        return blockNumbers;
    }


    /// <summary>
    /// Gets named fork property name for a fork
    /// </summary>
    public static string GetNamedForkProperty(string forkName)
    {
        return forkName switch
        {
            "berlin" => nameof(ChainSpecParamsJson.BerlinBlockNumber),
            "london" => nameof(ChainSpecParamsJson.LondonBlockNumber),
            "shanghai" => nameof(ChainSpecParamsJson.ShanghaiTimestamp),
            "cancun" => nameof(ChainSpecParamsJson.CancunTimestamp),
            "dencun" => nameof(ChainSpecParamsJson.DencunTimestamp),
            "prague" => nameof(ChainSpecParamsJson.PragueTimestamp),
            "osaka" => nameof(ChainSpecParamsJson.OsakaTimestamp),
            _ => throw new ArgumentException($"Unknown fork name: {forkName}")
        };
    }

    /// <summary>
    /// Checks if fork uses timestamp-based activation
    /// </summary>
    public static bool IsTimestampBased(string forkName) => TimestampBasedForks.ContainsKey(forkName.ToLower());

    /// <summary>
    /// Checks if fork uses block-based activation
    /// </summary>
    public static bool IsBlockBased(string forkName) => BlockBasedForks.ContainsKey(forkName.ToLower());
}
