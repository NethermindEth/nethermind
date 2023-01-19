// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class DepositMessageExtensions
    {
        public static Root HashTreeRoot(this DepositMessage item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this DepositMessage item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(DepositMessage item)
        {
            yield return new SszBasicVector(item.PublicKey.AsSpan());
            yield return new SszBasicVector(item.WithdrawalCredentials.AsSpan());
            yield return new SszBasicElement(item.Amount);
        }
    }
}
