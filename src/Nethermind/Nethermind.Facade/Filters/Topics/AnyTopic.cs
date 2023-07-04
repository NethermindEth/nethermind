// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class AnyTopic : TopicExpression
    {
        public static readonly AnyTopic Instance = new();

        private AnyTopic() { }

        public override bool Accepts(Keccak topic) => true;
        public override bool Accepts(ref KeccakStructRef topic) => true;

        public override bool Matches(Bloom bloom) => true;
        public override bool Matches(ref BloomStructRef bloom) => true;

        public override string ToString() => "null";
    }
}
