// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Synchronization.Test;

/// <summary>A block access list in its canonical encoding and in an encoding that <c>engine_newPayload</c> rejects.</summary>
internal static class BlockAccessListEncodings
{
    /// <summary>
    /// One account, <c>0x00..01</c>, with a balance change to <c>0x80</c> at index 0 and a nonce change to 5 at index 1.
    /// </summary>
    public static byte[] Canonical => Bytes.FromHexString("0xe2e1940000000000000000000000000000000000000001c0c0c4c3808180c3c20105c0");

    /// <summary>The same list with the balance written with a leading zero byte, <c>0x820080</c> instead of <c>0x8180</c>.</summary>
    public static byte[] LeadingZero => Bytes.FromHexString("0xe3e2940000000000000000000000000000000000000001c0c0c5c480820080c3c20105c0");
}
