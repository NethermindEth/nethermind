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

using System.Diagnostics;
using System.Threading.Tasks;

namespace Nethermind.Network.P2P
{
    public class Request<TMsg, TResult>
    {
        public Request(TMsg message)
        {
            CompletionSource = new TaskCompletionSource<TResult>();
            Message = message;
        }

        public void StartMeasuringTime()
        {
            Stopwatch = Stopwatch.StartNew();
        }

        public long FinishMeasuringTime()
        {
            Stopwatch.Stop();
            return Stopwatch.ElapsedMilliseconds;
        }

        private Stopwatch Stopwatch { get; set; }
        public long ResponseSize { get; set; }
        public TMsg Message { get; }
        public TaskCompletionSource<TResult> CompletionSource { get; }
    }
}
