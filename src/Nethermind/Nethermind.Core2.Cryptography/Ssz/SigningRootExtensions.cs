// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class SigningRootExtensions
    {
        public static Root HashTreeRoot(this SigningRoot item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item)));
            return new Root(tree.HashTreeRoot());
        }

        private static IEnumerable<SszElement> GetValues(SigningRoot item)
        {
            yield return item.ObjectRoot.ToSszBasicVector();
            yield return item.Domain.ToSszBasicVector();
        }
    }
}
