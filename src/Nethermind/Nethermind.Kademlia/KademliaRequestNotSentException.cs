// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Indicates that local admission prevented a request from being sent to a peer.
/// </summary>
public sealed class KademliaRequestNotSentException() : Exception("The request could not be sent locally.");
