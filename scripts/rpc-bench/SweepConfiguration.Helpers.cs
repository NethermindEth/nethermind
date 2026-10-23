// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal sealed record SweepArm(string Client, string Image, string Environment, string Flags, string Label)
{
    internal static SweepArm Parse(string entry, string defaultImage)
    {
        var flagIndex = entry.IndexOf('+');
        var flags = flagIndex < 0 ? "" : entry[(flagIndex + 1)..];
        var specification = flagIndex < 0 ? entry : entry[..flagIndex];
        var environmentIndex = specification.IndexOf('#');
        var environment = environmentIndex < 0 ? "" : specification[(environmentIndex + 1)..];
        specification = environmentIndex < 0 ? specification : specification[..environmentIndex];
        var imageIndex = specification.IndexOf('@');
        var client = imageIndex < 0 ? specification : specification[..imageIndex];
        var image = imageIndex < 0 ? defaultImage : specification[(imageIndex + 1)..];
        if (client is not ("nethermind" or "geth" or "reth")) throw new ArgumentException($"unknown sweep client type '{client}'");
        if (image.Length == 0) throw new ArgumentException("sweep client entry has an empty image");
        if (flagIndex >= 0 && flags.Split(';').Any(flag => !Regex.IsMatch(flag, @"^--[^\s;#]+$"))) throw new ArgumentException("malformed arm flag list");
        var label = imageIndex < 0 ? client : client + "_" + Regex.Replace(image[(image.LastIndexOf(':') + 1)..], "[^a-zA-Z0-9]", "_");
        if (environment.Length > 0)
        {
            var suffix = Regex.Replace(environment, "NETHERMIND_[A-Z]+CONFIG_", "");
            label += "_" + Regex.Replace(Regex.Replace(suffix, "[^a-zA-Z0-9]", "_"), "_+", "_").TrimEnd('_');
        }
        if (flags.Length > 0)
        {
            var readable = Regex.Replace(flags, "[^a-zA-Z0-9]", "_");
            label += "_" + readable[..Math.Min(24, readable.Length)] + "_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(flags)))[..12];
        }
        return new(client, image, environment.Replace(',', ' '), flags.Replace(';', ' '), label);
    }
}

internal sealed class SweepConfiguration
{
    internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    internal string this[string name] => Values[name];
    internal SweepArm[] Arms { get; }
    internal long[] Rates { get; }
    internal bool Corpus => this["JB_ETH_CALL_CORPUS"] == "true";
    internal long Number(string key) => long.Parse(this[key], CultureInfo.InvariantCulture);
    internal string Snapshot(string client) => Path.Combine(this["SNAPSHOT_ROOT"], client == "nethermind" && this["STATE_LAYOUT"] == "flat" ? $"nethermind-flat-{this["SNAPSHOT_BLOCK"]}" : $"{client}-{this["SNAPSHOT_BLOCK"]}");
    internal string Isolation(string client) => this["DB_ISOLATION_ALL"].Length > 0 ? this["DB_ISOLATION_ALL"] : client == "reth" ? "direct" : "overlay";

    internal SweepConfiguration(Func<string, string?> environment)
    {
        string Set(string key, string fallback = "", bool preserveEmpty = false)
        {
            var value = environment(key);
            return Values[key] = value is null || (!preserveEmpty && value.Length == 0) ? fallback : value;
        }
        foreach (var required in new[] { "OUT_DIR", "STATE_ROOT", "SCRATCH_ROOT", "NM_IMAGE", "SNAPSHOT_BLOCK", "JB_BENCHMARK_CONFIG" })
            if (Set(required).Length == 0) throw new ArgumentException(required + " is required");
        Set("CLIENTS", "nethermind"); Set("ROUNDS", "1"); Set("RPS_LIST", "100 250 500", true); Set("ISO_CONFIGS");
        Set("STATE_LAYOUT", "flat"); Set("JB_DURATION", "60s"); Set("ISO_DURATION", "20s"); Set("JB_REF"); Set("JB_SEED", "1");
        Set("NETWORK", "mainnet"); Set("JSONRPC_MODULES", "Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug"); Set("HEALTH_TIMEOUT", "1800");
        Set("DIAG_DIR", Path.Combine(this["SCRATCH_ROOT"], "diag")); Set("JB_ETH_CALL_CORPUS", "false");
        Set("CORPUS_DIR", "/data/expb-data/rpc-bench"); Set("CORPUS_GLOB", "eth-call-corpus*.jsonl.gz"); Set("CORPUS_METHOD", "eth_call");
        Set("CORPUS_TRACER", "callTracer", true); Set("CORPUS_TRACE_TYPES", "trace", true); Set("CORPUS_REQUESTS"); Set("CORPUS_PASSES"); Set("CORPUS_TIMINGS_PASSES");
        Set("CORPUS_TIMINGS_RPS", "0"); Set("CORPUS_TIMINGS_CONCURRENCY", "16"); Set("CORPUS_WARMUP_RPS_MAX", "0"); Set("CORPUS_PARITY_DIFFS", "false");
        Set("CORPUS_RESOURCE_SAMPLING", "true"); Set("CORPUS_WARMUP_DURATION", "60s"); Set("CORPUS_WARMUP_RPS", "400");
        Set("CORPUS_BASELINE", "none"); Set("CORPUS_BASELINE_DIR", Path.Combine(this["CORPUS_DIR"], "baselines"));
        Set("DB_ISOLATION_ALL"); Set("DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION", "false"); Set("SNAPSHOT_ROOT", "/data/nethermind");
        foreach (var name in new[] { "ROUNDS", "CORPUS_REQUESTS", "CORPUS_PASSES", "CORPUS_TIMINGS_PASSES", "CORPUS_TIMINGS_CONCURRENCY", "CORPUS_WARMUP_RPS" })
            if (this[name].Length > 0) Integer(name, this[name], true);
        foreach (var name in new[] { "CORPUS_TIMINGS_RPS", "CORPUS_WARMUP_RPS_MAX", "JB_SEED" }) Integer(name, this[name], false);
        if (!Regex.IsMatch(this["CORPUS_WARMUP_DURATION"], "^[0-9]+s?$")) throw new ArgumentException("corpus_warmup_duration must be integer seconds");
        Values["WARMUP_SECONDS"] = this["CORPUS_WARMUP_DURATION"].TrimEnd('s');
        Integer("WARMUP_SECONDS", this["WARMUP_SECONDS"], false);
        if (this["CORPUS_REQUESTS"].Length > 0 && this["CORPUS_PASSES"].Length > 0) throw new ArgumentException("corpus_requests and corpus_passes are mutually exclusive");
        if (this["STATE_LAYOUT"] is not ("flat" or "halfpath")) throw new ArgumentException("state_layout must be flat or halfpath");
        if (this["JB_ETH_CALL_CORPUS"] is not ("true" or "false")) throw new ArgumentException("JB_ETH_CALL_CORPUS must be true or false");
        if (this["CORPUS_BASELINE"] is not ("none" or "save" or "use")) throw new ArgumentException("CORPUS_BASELINE must be none, save or use");
        if (this["CORPUS_METHOD"] is not ("eth_call" or "debug_traceCall" or "trace_call")) throw new ArgumentException("invalid CORPUS_METHOD");
        if (this["CORPUS_METHOD"] != "eth_call" && (!Corpus || this["CORPUS_PARITY_DIFFS"] == "true")) throw new ArgumentException("trace replay requires a corpus and cannot use parity_diffs");
        if (this["DB_ISOLATION_ALL"] == "direct" && this["DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION"] != "true") throw new ArgumentException("DB_ISOLATION_ALL=direct mutates the shared snapshot; explicit consent is required");
        if (this["CLIENTS"].Contains('\n') || this["CLIENTS"].Contains('\r')) throw new ArgumentException("clients must be a single line");
        Arms = Words(this["CLIENTS"]).Select(entry => SweepArm.Parse(entry, this["NM_IMAGE"])).ToArray();
        if (Arms.Length == 0) throw new ArgumentException("clients must contain at least one entry");
        Rates = Words(this["RPS_LIST"]).Select(rate => Integer("rps_list entry", rate, true)).ToArray();
    }

    internal static string[] Words(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    internal static long Integer(string name, string value, bool positive) => Regex.IsMatch(value, positive ? "^[1-9][0-9]*$" : "^[0-9]+$") && long.TryParse(value, out var number) ? number : throw new ArgumentException($"{name} must be a {(positive ? "positive" : "non-negative")} integer");
    internal static string CorpusLabel(string path)
    {
        var label = Path.GetFileName(path);
        if (label.StartsWith("eth-call-corpus", StringComparison.Ordinal)) label = label[15..];
        if (label.StartsWith('-')) label = label[1..];
        if (label.EndsWith(".jsonl.gz", StringComparison.Ordinal)) label = label[..^9];
        return Regex.Replace(label.Length == 0 ? "default" : label, "[^a-zA-Z0-9._\n]", "-");
    }
}
