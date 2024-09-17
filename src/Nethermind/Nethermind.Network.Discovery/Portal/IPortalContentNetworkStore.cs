// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Content provider to be used when serving content to peer. Each content network (like history) is expected to
/// provide a separate implementation.
/// </summary>
public interface IPortalContentNetworkStore
{
    public byte[]? GetContent(byte[] contentKey);
    bool ShouldAcceptOffer(byte[] offerContentKey);
    void Store(byte[] contentKey, byte[] content);
}
