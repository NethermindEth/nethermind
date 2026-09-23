// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <inheritdoc/>
/// <remarks>
/// The flat backend's own signal, distinguished from transient gather failures (e.g. timeout under load), which
/// remain plain <see cref="InvalidOperationException"/>.
/// </remarks>
internal sealed class StateUnavailableException(string message) : Nethermind.Core.Exceptions.StateUnavailableException(message);
