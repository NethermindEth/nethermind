// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public struct ItemOrRootStruct<T> where T : struct
    {
        public T? Item { get; set; }

        public Root? Root { get; set; }
    }
}
