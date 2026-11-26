// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Config;

/// <summary>
/// The transport that RLPx connections run on.
/// </summary>
public enum RlpxTransport
{
    /// <summary>DotNetty channels.</summary>
    DotNetty,

    /// <summary>Sockets and System.IO.Pipelines, without DotNetty.</summary>
    Sockets
}
