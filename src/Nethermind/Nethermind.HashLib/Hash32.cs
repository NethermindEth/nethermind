// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.HashLib
{
    public abstract class Hash32 : Hash<int>, IHash<int>
    {
        public Hash32(Type a_baseImplementation, bool a_isBuildIn, int a_blockSize)
            : base(a_baseImplementation, a_isBuildIn, a_blockSize, 4)
        {
        }
    }
}
