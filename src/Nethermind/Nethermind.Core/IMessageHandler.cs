// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Handles one message.
/// </summary>
public interface IMessageHandler<TMessage>
{
    void HandleMessage(TMessage message);
}

/// <summary>
/// Handles multiple resources that can be represented as individual messages.
/// </summary>
public interface IBatchMessageHandler<TMessage, TResourceId> : IMessageHandler<TMessage>
{
    /// <summary>
    /// Handles a synchronous batch of resource identifiers that can each be represented as a single message.
    /// </summary>
    /// <param name="resourceIds">Resource identifiers valid only for the duration of this call; copy them before deferring work.</param>
    void HandleMessages(ReadOnlySpan<TResourceId> resourceIds);
}
