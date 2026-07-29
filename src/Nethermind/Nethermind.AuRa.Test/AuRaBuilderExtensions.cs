// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.AuRa;
using Nethermind.Core.Test.Builders;

namespace Nethermind.AuRa.Test;

public static class AuRaBuilderExtensions
{
    public static BlockHeaderBuilder WithAura(this BlockHeaderBuilder builder, ulong step, byte[]? signature = null)
    {
        AuRaBlockHeader header = AuRaBlockHeader.UpgradeFrom(builder.TestObjectInternal);
        header.AuRaStep = step;
        header.AuRaSignature = signature;
        builder.TestObjectInternal = header;
        return builder;
    }

    public static BlockBuilder WithAura(this BlockBuilder builder, ulong step, byte[]? signature = null)
    {
        AuRaBlockHeader header = AuRaBlockHeader.UpgradeFrom(builder.TestObjectInternal.Header);
        header.AuRaStep = step;
        header.AuRaSignature = signature;
        builder.TestObjectInternal = builder.TestObjectInternal.WithReplacedHeader(header);
        return builder;
    }
}
