// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;

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
}
