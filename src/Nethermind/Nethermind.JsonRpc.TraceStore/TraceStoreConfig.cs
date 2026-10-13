// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceStoreConfig : ITraceStoreConfig
{
    public bool Enabled { get; set; }
    public ulong BlocksToKeep { get; set; } = 10000;
    public ParityTraceTypes TraceTypes { get; set; } = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;
    /// <inheritdoc />
    public bool AddressIndexEnabled { get; set; }
    /// <inheritdoc />
    public int AddressIndexFromBlock { get; set; } = 1;
    /// <inheritdoc />
    public int AddressIndexLag { get; set; } = 64;
    /// <inheritdoc />
    public bool AddressIndexReset { get; set; }
    public bool VerifySerialized { get; set; } = false;
    public int MaxDepth { get; set; } = ParityLikeTraceSerializer.DefaultDepth;
    public int DeserializationParallelization { get; set; } = 0;
}
