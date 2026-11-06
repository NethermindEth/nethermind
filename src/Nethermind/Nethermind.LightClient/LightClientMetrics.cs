// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Prometheus;
using PrometheusMetrics = Prometheus.Metrics;

namespace Nethermind.LightClient;

/// <summary>Exports bounded process, synchronization and RPC metrics from the existing HTTP listener.</summary>
internal sealed class LightClientMetrics
{
    private const string Prefix = "nethermind_light_client_";
    private static readonly string[] Stages = ["checkpoint", "beacon", "bootstrap", "execution", "running", "stopping", "failed"];
    private static readonly string[] Outcomes = ["ok", "error", "cancelled"];
    private static readonly string[] Methods =
    [
        "eth_chainId", "net_version", "web3_clientVersion", "eth_accounts", "net_listening", "eth_blockNumber", "eth_syncing",
        "eth_getBalance", "eth_getTransactionCount", "eth_getStorageAt", "eth_getCode", "eth_getProof", "eth_call", "eth_estimateGas",
        "eth_getBlockByNumber", "eth_getBlockByHash", "eth_getBlockTransactionCountByNumber", "eth_getBlockTransactionCountByHash",
        "eth_getTransactionByBlockNumberAndIndex", "eth_getTransactionByBlockHashAndIndex", "eth_getTransactionByHash",
        "eth_getTransactionReceipt", "eth_getBlockReceipts", "eth_getLogs", "eth_feeHistory", "eth_gasPrice",
        "eth_maxPriorityFeePerGas", "eth_blobBaseFee", "eth_sendRawTransaction", "eth_subscribe", "eth_unsubscribe", "other",
    ];

    private readonly Func<ClientProgress> _readStatus;
    private readonly Gauge _ready;
    private readonly Gauge _initialized;
    private readonly Gauge _stage;
    private readonly Gauge _headBlock;
    private readonly Gauge _headSlot;
    private readonly Gauge _headAvailable;
    private readonly Gauge _headFresh;
    private readonly Gauge _hostedAccounts;
    private readonly Gauge _hostedStorage;
    private readonly Gauge.Child _beaconPeers;
    private readonly Gauge.Child _executionPeers;
    private readonly Gauge.Child _snapPeers;
    private readonly Dictionary<string, (Counter.Child[] Requests, Histogram.Child[] Duration)> _rpc = new(StringComparer.Ordinal);
    private readonly Counter _http;

    internal CollectorRegistry Registry { get; } = PrometheusMetrics.NewCustomRegistry();

    internal LightClientMetrics(string network, Func<ClientProgress> readStatus)
    {
        if (network is not ("mainnet" or "sepolia" or "hoodi")) throw new ArgumentException("Unknown light-client network.", nameof(network));
        _readStatus = readStatus;
        Registry.SetStaticLabels(new Dictionary<string, string> { ["network"] = network });
        MetricFactory factory = PrometheusMetrics.WithCustomRegistry(Registry);
        _http = factory.CreateCounter(Prefix + "http_requests_total", "HTTP RPC requests including admission, body and envelope failures.", ["outcome"]);
        foreach (string outcome in new[] { "completed", "overloaded", "too_large", "parse_error", "invalid_batch", "timed_out", "cancelled", "failed" })
            _http.WithLabels(outcome);
        _ready = factory.CreateGauge(Prefix + "ready", "One when the running client has a fresh authenticated optimistic head.");
        _initialized = factory.CreateGauge(Prefix + "initialized", "One when RPC initialization completed and the client is running.");
        _stage = factory.CreateGauge(Prefix + "startup_stage", "One for the current startup or lifecycle stage.", ["stage"]);
        _headBlock = factory.CreateGauge(Prefix + "head_block_number", "Last authenticated execution block number, or zero when unavailable.", ["head"]);
        _headSlot = factory.CreateGauge(Prefix + "head_beacon_slot", "Last authenticated beacon slot, or zero when unavailable.", ["head"]);
        _headAvailable = factory.CreateGauge(Prefix + "head_available", "One when the selected authenticated head is known.", ["head"]);
        _headFresh = factory.CreateGauge(Prefix + "head_fresh", "One when the selected authenticated head passes its RPC freshness policy.", ["head"]);
        _hostedAccounts = factory.CreateGauge(Prefix + "hosted_accounts", "Configured hosted accounts, current snapshots and failed refreshes.", ["state"]);
        _hostedStorage = factory.CreateGauge(Prefix + "hosted_storage_slots", "Nonzero storage slots retained across hosted account snapshots.");
        Gauge peers = factory.CreateGauge(Prefix + "peers", "Connected beacon, execution and SNAP protocol peers at the last sample.",
            new GaugeConfiguration { LabelNames = ["layer"], SuppressInitialValue = true });
        _beaconPeers = peers.WithLabels("beacon");
        _executionPeers = peers.WithLabels("execution");
        _snapPeers = peers.WithLabels("snap");
        Counter requests = factory.CreateCounter(Prefix + "rpc_requests_total", "Dispatched JSON-RPC items including notifications and errors.", ["method", "outcome"]);
        Histogram duration = factory.CreateHistogram(Prefix + "rpc_duration_seconds", "JSON-RPC item duration including initialization waits and failed requests.",
            new HistogramConfiguration
            {
                LabelNames = ["method", "outcome"],
                Buckets = [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 90],
            });
        foreach (string method in Methods)
        {
            Counter.Child[] counts = new Counter.Child[Outcomes.Length];
            Histogram.Child[] durations = new Histogram.Child[Outcomes.Length];
            for (int i = 0; i < Outcomes.Length; i++)
            {
                counts[i] = requests.WithLabels(method, Outcomes[i]);
                durations[i] = duration.WithLabels(method, Outcomes[i]);
            }
            _rpc.Add(method, (counts, durations));
        }
        DotNetStats.Register(Registry);
        Registry.AddBeforeCollectCallback(UpdateSnapshot);
    }

    internal Task WriteAsync(HttpContext context)
    {
        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        return Registry.CollectAndExportAsTextAsync(context.Response.Body, context.RequestAborted);
    }

    internal void SetPeers(int beacon, int execution, int snap)
    {
        _beaconPeers.Set(beacon);
        _executionPeers.Set(execution);
        _snapPeers.Set(snap);
    }

    internal void RecordRpc(string method, string outcome, double seconds)
    {
        if (!_rpc.TryGetValue(method, out (Counter.Child[] Requests, Histogram.Child[] Duration) series)) series = _rpc["other"];
        int index = outcome switch { "ok" or "notification" => 0, "cancelled" => 2, _ => 1 };
        series.Requests[index].Inc();
        series.Duration[index].Observe(seconds);
    }

    internal void RecordHttp(string outcome) => _http.WithLabels(outcome).Inc();

    private void UpdateSnapshot()
    {
        ClientProgress status = _readStatus();
        bool initialized = status.RpcInitialized && status.StartupStage == "running";
        _initialized.Set(initialized ? 1 : 0);
        _ready.Set(initialized && status.Latest?.Fresh == true ? 1 : 0);
        foreach (string stage in Stages) _stage.WithLabels(stage).Set(status.StartupStage == stage ? 1 : 0);
        UpdateHead("latest", status.Latest);
        UpdateHead("finalized", status.Finalized);
        int current = 0, failed = 0;
        long slots = 0;
        foreach (HostedAccountProgress account in status.HostedAccounts)
        {
            if (account.Current) current++;
            if (account.Stage == "failed") failed++;
            slots += account.StorageSlots;
        }
        _hostedAccounts.WithLabels("total").Set(status.HostedAccounts.Length);
        _hostedAccounts.WithLabels("current").Set(current);
        _hostedAccounts.WithLabels("failed").Set(failed);
        _hostedStorage.Set(slots);
        if (status.StartupStage is "stopping" or "failed") SetPeers(0, 0, 0);
    }

    private void UpdateHead(string head, HeadProgress? progress)
    {
        _headBlock.WithLabels(head).Set(progress?.BlockNumber ?? 0);
        _headSlot.WithLabels(head).Set(progress?.BeaconSlot ?? 0);
        _headAvailable.WithLabels(head).Set(progress is not null ? 1 : 0);
        _headFresh.WithLabels(head).Set(progress?.Fresh == true ? 1 : 0);
    }
}
