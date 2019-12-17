﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.HashLib.Extensions;

namespace Nethermind.HashLib.Crypto
{
    internal class SHA512 : SHA512Base
    {
        public SHA512()
            : base(64)
        {
        }

        protected override byte[] GetResult()
        {
            return Converters.ConvertULongsToBytesSwapOrder(m_state);
        }

        public override void Initialize()
        {
            m_state[0] = 0x6a09e667f3bcc908;
            m_state[1] = 0xbb67ae8584caa73b;
            m_state[2] = 0x3c6ef372fe94f82b;
            m_state[3] = 0xa54ff53a5f1d36f1;
            m_state[4] = 0x510e527fade682d1;
            m_state[5] = 0x9b05688c2b3e6c1f;
            m_state[6] = 0x1f83d9abfb41bd6b;
            m_state[7] = 0x5be0cd19137e2179;

            base.Initialize();
        }
    }
}
