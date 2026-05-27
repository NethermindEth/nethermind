// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.StateDiffsWriter.Service;

namespace Nethermind.StateDiffsWriter;

/// <summary>
/// v19 sidecar-feed plugin. Writes per-block <c>(CodeHashChange[], SlotCountChange[])</c>
/// records to a dedicated <c>BlockDiffs</c> RocksDB column family that the external
/// Go sidecar consumes via secondary-mode iteration. The plugin has no tracker, no
/// metrics, no RPC and no bootstrap — every aggregation moves out of process.
/// </summary>
public class StateDiffsWriterPlugin(IStateDiffsWriterConfig config) : INethermindPlugin
{
    public string Name => "StateDiffsWriter";
    public string Description => "Per-block state-diff writer feeding the v19 external sidecar";
    public string Author => "Nethermind";

    public bool Enabled => config.Enabled;
    public bool MustInitialize => true;
    public IModule Module => new StateDiffsWriterModule();

    public Task Init(INethermindApi nethermindApi)
    {
        // Resolving the writer service eagerly forces the NewHeadBlock subscription
        // to attach before the first head is produced. The DI container holds it as
        // a singleton; container shutdown invokes IDisposable on both the writer and
        // the pruner so we do not need an explicit teardown hook here.
        _ = nethermindApi.Context.Resolve<DiffsWriterService>();
        DiffsPruner pruner = nethermindApi.Context.Resolve<DiffsPruner>();
        pruner.Start();
        return Task.CompletedTask;
    }

    public Task InitRpcModules() => Task.CompletedTask;
}
