// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Optimum.Test;

public sealed record OptimumNodeMessage
{
    /// <summary>
    /// Topic name where the message was published.
    /// </summary>
    public required string Topic { get; init; }

    /// <summary>
    /// ID of the node that sent the message.
    /// </summary>
    public required string SourceNodeID { get; init; }

    /// <summary>
    /// Unique identifier for the message.
    /// </summary>
    // TODO: Contents do not look like valid UTF-8.
    // Also, message IDs are not unique, or based on the content of the `Message` itself (ex. like a hash)
    public required string MessageID { get; init; }

    /// <summary>
    /// Actual message data.
    /// </summary>
    public required byte[] Message { get; init; }
}
