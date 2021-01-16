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

namespace Nethermind.Evm.Tracing.ParityStyle
{
    //        {
//            "cost": 0.0,
//            "ex": {
//                "mem": null,
//                "push": [],
//                "store": null,
//                "used": 16961.0
//            },
//            "pc": 526.0,
//            "sub": null
//        }
    public class ParityVmOperationTrace
    {
        public long Cost { get; set; }
        public ParityMemoryChangeTrace Memory { get; set; }
        public byte[][] Push { get; set; }
        public ParityStorageChangeTrace Store { get; set; }
        public long Used { get; set; }
        public int Pc { get; set; }
        public ParityVmTrace Sub { get; set; }
    }
}
