// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Carries an optional <see cref="WitnessCapturingWorldStateProxy"/> across DI scopes: the proxy
/// lives in the main-processing scope (only registered on EIP-7928 chains), but the JSON-RPC
/// handler is constructed in the root scope. A nullable factory can't be registered directly
/// because Autofac rejects null instance returns — this holder gives the same shape a stable type.
/// </summary>
public sealed record WitnessProxyResolver(WitnessCapturingWorldStateProxy? Proxy);
