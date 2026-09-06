// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc.P2P;

/// <summary>
/// XDC advertises the <c>eth</c> capability in a renumbered <c>1NN</c> version space that does not overlap
/// with mainline Ethereum, so an XDC node never negotiates <c>eth</c> with an <c>eth/66</c>+ client.
/// </summary>
public static class XdcProtocolVersions
{
    /// <summary>XDPoS 2.0 legacy: <c>eth/63</c> semantics, no fork ID in the handshake.</summary>
    public const byte Legacy = 100;

    /// <summary><c>eth/64</c> equivalent: EIP-2364 handshake with fork ID.</summary>
    public const byte Xdc164 = 164;

    /// <summary><c>eth/65</c> equivalent: EIP-2464 transaction announcements on relocated message codes.</summary>
    public const byte Xdc165 = 165;

    /// <summary>The lowest version that carries a fork ID in <c>Status</c>.</summary>
    public const byte FirstVersionWithForkId = Xdc164;

    /// <summary>Message ID space for versions up to <see cref="Xdc164"/>, whose highest code is <see cref="XdcMessageCode.SyncInfoMsg"/>.</summary>
    public const int LegacyMessageIdSpaceSize = XdcMessageCode.SyncInfoMsg + 1;

    /// <summary>Message ID space for <see cref="Xdc165"/>, whose highest code is <see cref="XdcMessageCode.PooledTransactions"/>.</summary>
    public const int MessageIdSpaceSize = XdcMessageCode.PooledTransactions + 1;

    /// <summary>Whether <paramref name="protocolVersion"/> is one of XDC's <c>eth</c> versions.</summary>
    /// <remarks>
    /// Sync-side capability checks compare against the mainline <c>ethNN</c> numbers, which XDC's <c>1NN</c>
    /// versions sit above; anything gated on such a comparison has to ask this instead.
    /// </remarks>
    public static bool IsXdcVersion(byte protocolVersion) => protocolVersion is Legacy or Xdc164 or Xdc165;
}
