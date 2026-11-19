// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// The connection to a peer that a session runs on.
/// </summary>
public interface IRlpxConnection
{
    /// <summary>
    /// Gets whether the connection can take more outgoing data: false while too much is waiting to be sent, and once the connection has closed.
    /// </summary>
    bool IsWritable { get; }

    /// <summary>
    /// Compresses the messages sent and decompresses the messages received from now on with Snappy.
    /// </summary>
    void EnableSnappy();

    /// <summary>
    /// Closes the connection.
    /// </summary>
    /// <remarks>
    /// It takes no cancellation token because closing must always release the connection.
    /// </remarks>
    Task DisconnectAsync();
}
