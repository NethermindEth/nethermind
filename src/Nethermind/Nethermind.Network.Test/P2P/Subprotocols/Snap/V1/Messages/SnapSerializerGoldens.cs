// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V1.Messages;

/// <summary>
/// Shared inputs and hand-derived RLP fragments for the snap serializer goldens.
/// </summary>
/// <remarks>
/// Each golden fragment and its input come from one hex constant, so the
/// expectation cannot drift from the input. The values are verified with an
/// independent encoder (pyrlp + pycryptodome keccak).
/// </remarks>
internal static class SnapSerializerGoldens
{
    private const string EmptyStringKeccakHex = "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";
    private const string RangeStartHex = "15d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";
    private const string RangeLimitHex = "20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

    /// <summary>Request id 1111 as an RLP item: 0x82 length prefix + 0x0457.</summary>
    public const string RequestId1111Rlp = "820457";

    /// <summary>keccak("") as an RLP item: 0xa0 + 32 bytes.</summary>
    public const string EmptyStringKeccakRlp = "a0" + EmptyStringKeccakHex;

    /// <summary><see cref="RangeStart"/> as an RLP item.</summary>
    public const string RangeStartRlp = "a0" + RangeStartHex;

    /// <summary><see cref="RangeLimit"/> as an RLP item.</summary>
    public const string RangeLimitRlp = "a0" + RangeLimitHex;

    /// <summary>The starting hash the range request tests use.</summary>
    public static readonly Hash256 RangeStart = new(RangeStartHex);

    /// <summary>The limit hash the range request tests use.</summary>
    public static readonly Hash256 RangeLimit = new(RangeLimitHex);
}
