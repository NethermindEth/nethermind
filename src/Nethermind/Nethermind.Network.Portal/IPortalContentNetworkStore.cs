// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Portal;

/// <summary>
/// Content provider to be used when serving content to peer. Each content network (like history) is expected to
/// provide a separate implementation.
/// </summary>
public interface IPortalContentNetworkStore
{
    public byte[]? GetContent(ReadOnlySpan<byte> contentKey);
    bool ShouldAcceptOffer(ReadOnlySpan<byte> offerContentKey);
    void Store(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> content);
}
