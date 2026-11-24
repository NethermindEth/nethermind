// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters.Topics
{
    public class AnyTopicsFilter(params TopicExpression[] expressions) : TopicsFilter
    {
        public override IEnumerable<TopicExpression> Expressions => expressions;
        public override bool AcceptsAnyBlock => expressions.Length == 0 || expressions.Any(static e => e.AcceptsAnyBlock);

        public override bool Accepts(LogEntry entry) => Accepts(entry.Topics);

        private bool Accepts(Hash256[] topics)
        {
            for (int i = 0; i < expressions.Length; i++)
            {
                for (int j = 0; j < topics.Length; j++)
                {
                    if (expressions[i].Accepts(topics[j]))
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

            if (expressions.Length == 0)
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[32];
            KeccaksIterator iterator = new(entry.TopicsRlp, buffer);
            // Any expression against any topic, as for materialised topics. Topics in the outer loop: the iterator
            // only moves forward, and a topic stored without its leading zeros lives in the buffer until the next read.
            while (iterator.TryGetNext(out Hash256StructRef keccak))
            {
                for (int i = 0; i < expressions.Length; i++)
                {
                    if (expressions[i].Accepts(ref keccak))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public override bool Matches(Bloom bloom)
        {
            bool result = true;

            for (int i = 0; i < expressions.Length; i++)
            {
                result = expressions[i].Matches(bloom);
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

            for (int i = 0; i < expressions.Length; i++)
            {
                result = expressions[i].Matches(ref bloom);
                if (result)
                {
                    break;
                }
            }

            return result;
        }
    }
}
