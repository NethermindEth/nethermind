// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core2.Crypto
{
    public struct OpenRoot
    {
        public UInt256 Root { get; set; }
        public int Size { get; set; }

        public Root Close()
        {
            throw new NotImplementedException();
        }
    }
}
