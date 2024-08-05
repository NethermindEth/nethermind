// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Provide a high level interface to interact with portal content network.
/// </summary>
public interface IPortalContentNetwork
{
    Task<byte[]?> LookupContent(byte[] contentKey, CancellationToken token);
    Task<byte[]?> LookupContentFrom(IEnr node, byte[] contentKey, CancellationToken token);
    Task BroadcastContent(byte[] contentKey, byte[] value, CancellationToken token);

    void AddOrRefresh(IEnr node);
    Task Run(CancellationToken token);
    Task Bootstrap(CancellationToken token);

    /// <summary>
    /// Content provider to be used when serving content to peer.
    /// </summary>
    public interface Store
    {
        public byte[]? GetContent(byte[] contentKey);
        bool ShouldAcceptOffer(byte[] offerContentKey);
        void Store(byte[] contentKey, byte[] content);
    }
}
