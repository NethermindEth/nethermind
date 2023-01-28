// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class SignedBeaconBlockHeaderExtensions
    {
        public static Root HashTreeRoot(this SignedBeaconBlockHeader item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item)));
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this SignedBeaconBlockHeader item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(SignedBeaconBlockHeader item)
        {
            yield return item.Message.ToSszContainer();
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
