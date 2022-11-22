// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class DepositDataExtensions
    {
        public static Root HashTreeRoot(this IEnumerable<DepositData> list, ulong limit)
        {
            var tree = new SszTree(list.ToSszList(limit));
            return new Root(tree.HashTreeRoot());
        }

        public static Root HashTreeRoot(this DepositData item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this DepositData item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszList ToSszList(this IEnumerable<DepositData> list, ulong limit)
        {
            return new SszList(list.Select(x => ToSszContainer(x)), limit);
        }

        private static IEnumerable<SszElement> GetValues(DepositData item)
        {
            yield return new SszBasicVector(item.PublicKey.AsSpan());
            yield return new SszBasicVector(item.WithdrawalCredentials.AsSpan());
            yield return new SszBasicElement(item.Amount);
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
