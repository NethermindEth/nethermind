// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Provide a high level interface to interact with portal content network.
/// </summary>
public interface IPortalContentNetwork
{
    public Task<byte[]?> LookupContent(byte[] contentKey, CancellationToken token);
    public Task<byte[]?> LookupContentFrom(IEnr node, byte[] contentKey, CancellationToken token);

    public void AddOrRefresh(IEnr node);
    public Task Run(CancellationToken token);
    public Task Bootstrap(CancellationToken token);

    /// <summary>
    /// Content provider to be used when serving content to peer.
    /// </summary>
    public interface Store
    {
        public byte[]? GetContent(byte[] contentKey);
    }
}
