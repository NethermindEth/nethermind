// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters.Topics
{
    public class SpecificTopic(Hash256 topic) : TopicExpression
    {
        private readonly Hash256 _topic = topic;
        private readonly Bloom.BloomExtract _bloomExtract = Bloom.GetExtract(topic);

        public Hash256 Topic => _topic;
        public override bool AcceptsAnyBlock => false;

        public override bool Accepts(Hash256 topic) => topic == _topic;

        public override bool Accepts(ref Hash256StructRef topic) => topic == _topic;

        public override bool Matches(Bloom bloom) => bloom.Matches(_bloomExtract);

        public override bool Matches(ref BloomStructRef bloom) => bloom.Matches(_bloomExtract);

        private bool Equals(SpecificTopic other) => _topic.Equals(other._topic);

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((SpecificTopic)obj);
        }

        public override int GetHashCode() => _topic.GetHashCode();

        public override string ToString() => _topic.ToString();
    }
}
