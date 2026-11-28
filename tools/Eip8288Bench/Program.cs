// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Tools.Eip8288Bench;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        usage: Eip8288Bench <command> [--name=value ...]
          genesis   write a geth-style genesis with EIP-8288 at genesis and the load accounts funded
          load      scenario A: submit leanSPHINCS-claim frame transactions to running nodes and time their propagation
          summarize join a mempool run's load.json with its node snapshots
          aggregate mean and CV of repeated mempool runs of one arm
        """);
    return args.Length == 0 ? 1 : 0;
}

Options options = Options.Parse(args.AsSpan(1));
return args[0] switch
{
    "genesis" => MempoolLoad.WriteGenesis(options),
    "load" => await MempoolLoad.Execute(options),
    "summarize" => MempoolSummary.Execute(options),
    "aggregate" => MempoolSummary.Aggregate(options),
    _ => throw new ArgumentException($"unknown command {args[0]}")
};

namespace Nethermind.Tools.Eip8288Bench
{
    /// <summary><c>--name=value</c> command-line options.</summary>
    internal sealed class Options(Dictionary<string, string> values)
    {
        public static Options Parse(ReadOnlySpan<string> args)
        {
            Dictionary<string, string> values = [];
            foreach (string arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"expected --name=value, got {arg}");
                int split = arg.IndexOf('=');
                if (split < 0) values[arg[2..]] = "true";
                else values[arg[2..split]] = arg[(split + 1)..];
            }
            return new(values);
        }

        public string String(string name, string fallback) => values.GetValueOrDefault(name, fallback);
        public string? Optional(string name) => values.GetValueOrDefault(name);
        public string Path(string name, string fallback) => System.IO.Path.GetFullPath(String(name, fallback));
        public int Int(string name, int fallback) => values.TryGetValue(name, out string? v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
        public double Double(string name, double fallback) =>
            values.TryGetValue(name, out string? v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

        public int[] IntList(string name, int[] fallback)
        {
            if (!values.TryGetValue(name, out string? v)) return fallback;
            string[] parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int[] result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            return result;
        }
    }

    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
    }
}
