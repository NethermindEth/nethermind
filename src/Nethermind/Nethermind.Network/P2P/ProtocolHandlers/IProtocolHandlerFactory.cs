// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Network.P2P.ProtocolHandlers;

/// <summary>
/// Factory interface for creating protocol handlers.
/// Factories are registered via DI using the OrderedComponents pattern.
/// </summary>
public interface IProtocolHandlerFactory
{
    /// <summary>
    /// The protocol code this factory creates handlers for (e.g., "snap", "eth").
    /// </summary>
    string ProtocolCode { get; }

    /// <summary>
    /// Attempts to create a protocol handler for the specified session and version.
    /// Returns false if this factory doesn't support the requested version.
    /// </summary>
    /// <param name="session">The session requesting the protocol handler.</param>
    /// <param name="version">The protocol version requested.</param>
    /// <param name="handler">The created protocol handler, or null if version not supported.</param>
    /// <returns>True if handler was created; false if version not supported.</returns>
    bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler);
}
