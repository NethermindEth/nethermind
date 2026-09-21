// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>Flat-backend alias of <see cref="Nethermind.State.StateUnavailableException"/> so existing throw sites keep compiling.</summary>
public sealed class StateUnavailableException(string message) : Nethermind.State.StateUnavailableException(message);
