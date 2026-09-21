// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State;

/// <summary>
/// The requested state does not exist (pruned, not yet synced, or concurrently removed) and no retry can recover it —
/// as opposed to transient gather failures, which remain plain <see cref="InvalidOperationException"/>.
/// </summary>
public class StateUnavailableException(string message) : InvalidOperationException(message);
