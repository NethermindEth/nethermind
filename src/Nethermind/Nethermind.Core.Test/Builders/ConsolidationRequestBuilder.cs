// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test.Builders;
using Nethermind.Core.ConsensusRequests;

public class ConsolidationRequestBuilder : BuilderBase<ConsolidationRequest>
{
    public ConsolidationRequestBuilder() => TestObject = new();

    public ConsolidationRequestBuilder WithSourceAddress(Address sourceAddress)
    {
        TestObject.SourceAddress = sourceAddress;

        return this;
    }

    public ConsolidationRequestBuilder WithSourcePubkey(byte[] SourcePubkey)
    {
        TestObject.SourcePubkey = SourcePubkey;

        return this;
    }

    public ConsolidationRequestBuilder WithTargetPubkey(byte[] TargetPubkey)
    {
        TestObject.TargetPubkey = TargetPubkey;

        return this;
    }

}
