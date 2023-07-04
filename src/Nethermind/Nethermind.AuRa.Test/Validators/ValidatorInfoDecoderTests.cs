// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ValidatorInfoDecoderTests
    {
        [Test]
        public void Can_decode_previously_encoded()
        {
            ValidatorInfo info = new(10, 5, new[] { TestItem.AddressA, TestItem.AddressC });
            Rlp rlp = Rlp.Encode(info);
            ValidatorInfo decoded = Rlp.Decode<ValidatorInfo>(rlp);
            decoded.Should().BeEquivalentTo(info);
        }
    }
}
