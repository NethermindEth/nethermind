//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Threading;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Synchronization.BeamSync
{
    public static class BeamSyncContext
    {
        public static AsyncLocal<DateTime?> LastFetchUtc = new AsyncLocal<DateTime?>();
        public static AsyncLocal<string> Description = new AsyncLocal<string>();
        public static AsyncLocal<UInt256> MinimumDifficulty = new AsyncLocal<UInt256>();
        public static AsyncLocal<int?> LoopIterationsToFailInTest = new AsyncLocal<int?>();
        public static AsyncLocal<int> ResolvedInContext = new AsyncLocal<int>();
        public static AsyncLocal<CancellationToken> Cancelled = new AsyncLocal<CancellationToken>();

    }
}