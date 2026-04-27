// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Per-endpoint Geth error-message wrappers. Each Geth RPC handler wraps the bare
/// error from <c>core.ApplyMessage</c> differently before sending it on the wire;
/// Nethermind's RPC layer mirrors those wrappers here so every endpoint's wire format
/// matches Geth.
/// </summary>
internal static class ErrorWrapper
{
    public static string EthCall(string inner, long gasLimit) =>
        $"err: {inner} (supplied gas {gasLimit})";

    public static string EstimateGasBinarySearch(string inner, long gas) =>
        $"failed with {gas} gas: {inner}";

    public static string CreateAccessList(string inner, Hash256 txHash) =>
        $"failed to apply transaction: {txHash} err: {inner}";

    public static string DebugTrace(string inner) => $"tracing failed: {inner}";

    // eth_simulateV1: no wrap. Geth propagates the bare core error directly through
    // its simulate handler, so Nethermind matches by NOT wrapping for that endpoint.
}
