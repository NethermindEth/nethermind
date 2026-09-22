// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// The requested state is one this node legitimately does not hold: pruned or concurrently removed, below the flat
/// history retention floor, outside every retained slice, or not yet captured. JSON-RPC maps only this subtype to
/// resource-unavailable (-32002); every other <see cref="StateUnavailableException"/> keeps the resource-not-found
/// path and its WARN.
/// </summary>
public class StateNotRetainedException(string message) : StateUnavailableException(message);
