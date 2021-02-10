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

using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.HashLib;

namespace Nethermind.Crypto
{
    public static class Ripemd
    {
        private static readonly ThreadLocal<IHash> _ripemd160 = new();

        private static void InitIfNeeded()
        {
            if (!_ripemd160.IsValueCreated)
            {
                var ripemd = HashFactory.Crypto.CreateRIPEMD160();
                ripemd.Initialize();
                _ripemd160.Value = ripemd;
            }
        }
        
        public static byte[] Compute(byte[] input)
        {
            InitIfNeeded();
            return _ripemd160.Value.ComputeBytes(input).GetBytes();
        }

        public static string ComputeString(byte[] input)
        {
            return Compute(input).ToHexString(false);
        }
    }
}
