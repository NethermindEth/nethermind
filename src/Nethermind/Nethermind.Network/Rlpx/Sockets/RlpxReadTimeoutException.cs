// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.Rlpx.Sockets;

/// <summary>
/// Reports a connection on which nothing was received for the read timeout.
/// </summary>
internal sealed class RlpxReadTimeoutException() : Exception("No data received within the read timeout");
