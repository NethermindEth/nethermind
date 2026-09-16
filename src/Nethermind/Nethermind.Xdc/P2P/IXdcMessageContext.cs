// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// The parts of a protocol handler that <see cref="XdcConsensusMessageHandler"/> needs in order to decode
/// and trace messages on behalf of the handler that owns it.
/// </summary>
/// <remarks>
/// Each XDC protocol version derives from a different <c>ethNN</c> handler, so the shared behaviour cannot
/// live in a common base class and reaches the owning handler through this interface instead.
/// </remarks>
internal interface IXdcMessageContext
{
    /// <summary>Deserializes a message using the owning handler's error and limit reporting.</summary>
    T Decode<T>(IByteBuffer buffer) where T : P2PMessage;

    /// <summary>Traces an incoming message on the owning handler's session.</summary>
    void Report(MessageBase message, int size);

    /// <inheritdoc cref="Report(MessageBase, int)"/>
    void Report(string messageInfo, int size);
}
