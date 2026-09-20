// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconChain.Engine;

/// <summary>
/// The in-process <c>engine_newPayload</c> call itself failed, so the execution layer returned no
/// verdict on the payload.
/// </summary>
/// <remarks>
/// Distinct from the execution layer reporting SYNCING, which is a verdict - "not rejected, not
/// validated". A failed call means "cannot evaluate", and optimistic sync permits an optimistic
/// import only when the execution layer is syncing, not when the call to it errored. Deliberately
/// not a <c>BeaconStateException</c>: the block is not invalid, it is unevaluated, and must be
/// retried once the engine answers again.
/// </remarks>
public sealed class EngineUnavailableException(string method, string? error)
    : Exception($"In-process engine_{method} call returned no verdict: {error}");
