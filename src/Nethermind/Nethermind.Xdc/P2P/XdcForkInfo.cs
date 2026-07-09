// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Network;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// Delegates all fork ID operations to the inner <see cref="IForkInfo"/> but accepts every peer fork ID
/// as compatible, consistent with <see cref="XdcProtocolValidator.MustValidateForkId"/>.
/// </summary>
/// <remarks>
/// XDC runs a private fork schedule that does not match Ethereum mainnet. Peer fork IDs are already
/// validated (and intentionally skipped) at the ETH handshake layer via <see cref="XdcProtocolValidator"/>.
/// Filtering peers at the discovery layer based on fork ID would silently drop all XDC peers before Hello
/// is even exchanged, so the check is bypassed here as well.
/// </remarks>
internal sealed class XdcForkInfo(IForkInfo inner) : IForkInfo
{
    public ForkId GetForkId(ulong headNumber, ulong headTimestamp) =>
        inner.GetForkId(headNumber, headTimestamp);

    public Network.ValidationResult ValidateForkId(ForkId peerId, BlockHeader? head) =>
        inner.ValidateForkId(peerId, head);

    public ForkActivationsSummary GetForkActivationsSummary(BlockHeader? head) =>
        inner.GetForkActivationsSummary(head);

    /// <inheritdoc/>
    /// <remarks>Always returns <see langword="true"/>; see class remarks.</remarks>
    public bool IsForkIdCompatible(ForkId peerId) => true;
}
