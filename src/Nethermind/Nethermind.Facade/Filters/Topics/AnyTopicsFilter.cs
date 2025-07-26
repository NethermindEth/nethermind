// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class AnyTopicsFilter : TopicsFilter
    {
        private readonly TopicExpression[] _expressions;

        public AnyTopicsFilter(params TopicExpression[] expressions)
        {
            _expressions = expressions;
        }

        public override bool Accepts(LogEntry entry) => Accepts(entry.Topics);

        private bool Accepts(Hash256[] topics)
        {
            for (int i = 0; i < _expressions.Length; i++)
            {
                for (int j = 0; j < topics.Length; j++)
                {
                    if (_expressions[i].Accepts(topics[j]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public override bool Accepts(ref LogEntryStructRef entry)
        {
            if (entry.Topics is not null)
            {
                return Accepts(entry.Topics);
            }

            Span<byte> buffer = stackalloc byte[32];
            var iterator = new KeccaksIterator(entry.TopicsRlp, buffer);
            for (int i = 0; i < _expressions.Length; i++)
            {
                if (iterator.TryGetNext(out var keccak))
                {
                    if (_expressions[i].Accepts(ref keccak))
                    {
                        return true;
                    }
                }
                else
                {
                    return false;
                }
            }

            return false;
        }

        public override bool Matches(Bloom bloom)
        {
            bool result = true;

            for (int i = 0; i < _expressions.Length; i++)
            {
                result = _expressions[i].Matches(bloom);
                if (result)
                {
                    break;
                }
            }

            return result;
        }

        public override bool Matches(ref BloomStructRef bloom)
        {
            bool result = true;

            for (int i = 0; i < _expressions.Length; i++)
            {
                result = _expressions[i].Matches(ref bloom);
                if (result)
                {
                    break;
                }
            }

            return result;
        }

        public override bool AcceptsAnyBlock => _expressions.Length == 0 || _expressions.Any(e => e.AcceptsAnyBlock);

        public override IEnumerable<Hash256> Topics => _expressions.SelectMany(e => e.Topics);

        public override HashSet<int> FilterBlockNumbers(IReadOnlyDictionary<Hash256, List<int>> byTopic)
        {
            HashSet<int>? result = null;
            foreach (TopicExpression expression in _expressions)
            {
                if (result == null)
                    result = expression.FilterBlockNumbers(byTopic);
                else
                    result.UnionWith(expression.FilterBlockNumbers(byTopic));
            }

            return result ?? [];
        }
    }
}
