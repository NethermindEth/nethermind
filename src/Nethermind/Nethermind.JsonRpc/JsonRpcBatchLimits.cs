// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

/// <summary>Bounds the work and answered envelopes of one JSON-RPC batch.</summary>
/// <param name="MaxExecutions">The number of leading batch items that may dispatch a method.</param>
/// <param name="MaxEnvelopes">The number of items answered individually before one final limit response.</param>
public readonly record struct JsonRpcBatchLimits(int MaxExecutions, int MaxEnvelopes);
