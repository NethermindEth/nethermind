// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class SpecificTopic : TopicExpression
    {
        private readonly Keccak _topic;
        private Bloom.BloomExtract? _bloomExtract;

        public SpecificTopic(Keccak topic)
        {
            _topic = topic;
        }

        private Core.Bloom.BloomExtract BloomExtract => _bloomExtract ??= Bloom.GetExtract(_topic);

        public override bool Accepts(Keccak topic) => topic == _topic;

        public override bool Accepts(in ValueKeccak topic) => topic == _topic;

        public override bool Matches(Bloom bloom) => bloom.Matches(BloomExtract);

        public override bool Matches(ref BloomStructRef bloom) => bloom.Matches(BloomExtract);

        private bool Equals(SpecificTopic other) => _topic.Equals(other._topic);

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((SpecificTopic)obj);
        }

        public override int GetHashCode() => _topic.GetHashCode();

        public override string ToString() => _topic.ToString();
    }
}
