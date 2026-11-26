// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Synchronization.Test;

/// <summary>Fails every header decode with an exception that is not an RLP reader exception.</summary>
internal sealed class ThrowingHeaderDecoder : HeaderDecoder
{
    public static readonly BlockBodyDecoder BodyDecoder = new(new ThrowingHeaderDecoder());

    /// <summary>A valid body with one uncle, so that its decode reaches the header decoder.</summary>
    public static byte[] BodyWithUncle() =>
        BlockBodyDecoder.Instance.Encode(new BlockBody([], [Core.Test.Builders.Build.A.BlockHeader.TestObject]));

    protected override BlockHeader? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        throw new ArgumentException("Header decode failed");
}
