// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>
/// The requested state cannot serve the request — it does not exist (pruned or concurrently removed) or the backend
/// refuses it for that usage — and no retry can recover it, as opposed to transient gather failures (e.g. timeout
/// under load), which remain plain <see cref="InvalidOperationException"/>.
/// </summary>
internal sealed class StateUnavailableException(string message) : InvalidOperationException(message);
