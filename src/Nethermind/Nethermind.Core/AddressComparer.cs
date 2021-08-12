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

using System.Collections.Generic;

namespace Nethermind.Core
{
    public class AddressComparer : IComparer<Address>
    {
        private AddressComparer()
        {
        }
            
        public static AddressComparer Instance { get; } = new();

        public int Compare(Address? x, Address? y)
        {
            if (x is null)
            {
                return y is null ? 0 : -1;
            }
                
            if (y is null)
            {
                return 1;
            }
                
            for (int i = 0; i < Address.ByteLength; i++)
            {
                if (x.Bytes[i] < y.Bytes[i])
                {
                    return -1;
                }

                if (x.Bytes[i] > y.Bytes[i])
                {
                    return 1;
                }
            }
                
                
            return 0;
        }
    }
}
