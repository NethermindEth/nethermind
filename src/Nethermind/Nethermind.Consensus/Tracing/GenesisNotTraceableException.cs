// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Tracing;

/// <summary>Thrown when a tracer is asked to replay the genesis block, which has no parent state and no transactions.</summary>
/// <remarks>Callers at the RPC boundary should map this to a clean client-facing error (e.g. invalid input) rather than letting it surface as a generic internal error.</remarks>
public sealed class GenesisNotTraceableException : InvalidOperationException
{
    public GenesisNotTraceableException() : base("genesis is not traceable") { }
}
