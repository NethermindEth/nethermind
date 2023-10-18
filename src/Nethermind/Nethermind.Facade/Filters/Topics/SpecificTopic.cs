// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters.Topics
{
    public class SpecificTopic : TopicExpression
    {
        public Keccak Topic { get; }
        private Bloom.BloomExtract _bloomExtract;

        public SpecificTopic(Keccak topic)
        {
            Topic = topic;
        }

        private ref readonly Bloom.BloomExtract BloomExtract
        {
            get
            {
                if (_bloomExtract.IsZero())
                {
                    _bloomExtract = Bloom.GetExtract(Topic);
                }

                return ref _bloomExtract;
            }
        }

        public override bool Accepts(Keccak topic) => topic == Topic;

        public override bool Accepts(ref KeccakStructRef topic) => topic == Topic;

        public override bool Matches(Bloom bloom) => bloom.Matches(in BloomExtract);

        public override bool Matches(ref BloomStructRef bloom) => bloom.Matches(in BloomExtract);

        public override IEnumerable<Keccak> OrTopicExpression
        {
            get
            {
                yield return Topic;
            }
        }

        private bool Equals(SpecificTopic other) => Topic.Equals(other.Topic);

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((SpecificTopic)obj);
        }

        public override int GetHashCode() => Topic.GetHashCode();

        public override string ToString() => Topic.ToString();
    }
}
