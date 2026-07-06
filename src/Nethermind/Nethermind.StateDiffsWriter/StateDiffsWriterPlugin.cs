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
/// Writes per-block state-diff records to a dedicated <c>BlockDiffs</c> RocksDB column family read by
/// an external consumer. No tracker, RPC or bootstrap; aggregation runs out of process.
/// </summary>
public class StateDiffsWriterPlugin(IStateDiffsWriterConfig config) : INethermindPlugin
{
    public string Name => "StateDiffsWriter";
    public string Description => "Per-block state-diff writer feeding an external consumer";
    public string Author => "Nethermind";

    public bool Enabled => config.Enabled;
    public bool MustInitialize => true;
    public IModule Module => new StateDiffsWriterModule();

    public Task Init(INethermindApi nethermindApi)
    {
        // Resolve eagerly so the NewHeadBlock subscription attaches before the first head;
        // container shutdown disposes both singletons, so no explicit teardown is needed.
        _ = nethermindApi.Context.Resolve<DiffsWriterService>();
        DiffsPruner pruner = nethermindApi.Context.Resolve<DiffsPruner>();
        pruner.Start();
        return Task.CompletedTask;
    }
}
