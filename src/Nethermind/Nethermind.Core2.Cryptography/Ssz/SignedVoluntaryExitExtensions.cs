// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class SignedVoluntaryExitExtensions
    {
        public static Root HashTreeRoot(this SignedVoluntaryExit item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item)));
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this SignedVoluntaryExit item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszList ToSszList(this IEnumerable<SignedVoluntaryExit> list, ulong limit)
        {
            return new SszList(list.Select(x => ToSszContainer(x)), limit);
        }

        private static IEnumerable<SszElement> GetValues(SignedVoluntaryExit item)
        {
            yield return item.Message.ToSszContainer();
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
