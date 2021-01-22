//  Copyright (c) 2021 Demerzel Solutions Limited
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
    internal class SHA384 : SHA512Base
    {
        public SHA384()
            : base(48)
        {
        }

        protected override byte[] GetResult()
        {
            return Converters.ConvertULongsToBytesSwapOrder(m_state, 0, 6);
        }

        public override void Initialize()
        {
            m_state[0] = 0xcbbb9d5dc1059ed8;
            m_state[1] = 0x629a292a367cd507;
            m_state[2] = 0x9159015a3070dd17;
            m_state[3] = 0x152fecd8f70e5939;
            m_state[4] = 0x67332667ffc00b31;
            m_state[5] = 0x8eb44a8768581511;
            m_state[6] = 0xdb0c2e0d64f98fa7;
            m_state[7] = 0x47b5481dbefa4fa4;

            base.Initialize();
        }
    }
}
