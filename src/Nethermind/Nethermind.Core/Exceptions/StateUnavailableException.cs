// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

/// <summary>
/// The state a caller asked to open cannot serve the request — it does not exist (pruned or concurrently removed) or
/// the backend refuses it for that usage — and no retry can recover it.
/// </summary>
/// <remarks>
/// Thrown by the throwing scope-opening extensions, where an unavailable state is a caller error rather than an
/// ordinary answer; the <c>Try</c> shapes report the same condition as <c>false</c>. JSON-RPC answers it with
/// <c>ResourceUnavailable</c> rather than an internal error, since a pruned historical state is an ordinary request.
/// </remarks>
public class StateUnavailableException(string message) : InvalidOperationException(message);
