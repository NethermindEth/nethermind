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
// 

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public interface IBundlePool : IBundleSource
    {
        event EventHandler<BundleEventArgs> NewReceived;
        event EventHandler<BundleEventArgs> NewPending;
        bool AddBundle(MevBundle bundle);
        IEnumerable<MevBundle> GetBundles(long block, UInt256 timestamp, CancellationToken token = default);
    }
    
    public static class BundlePoolExtensions
    {
        public static IEnumerable<MevBundle> GetBundles(this IBundlePool bundleSource, BlockHeader parent, ITimestamper timestamper, CancellationToken token = default) => 
            bundleSource.GetBundles(parent.Number + 1, timestamper.UnixTime.Seconds, token);
    }
}
