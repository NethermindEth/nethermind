// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network;

/// <summary>
/// The resolved local and external IP addresses of this node.
/// </summary>
public readonly record struct NethermindIp(IPAddress LocalIp, IPAddress ExternalIp);
