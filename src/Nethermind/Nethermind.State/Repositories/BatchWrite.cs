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

using System;
using System.Threading;

namespace Nethermind.State.Repositories
{
    public class BatchWrite : IDisposable
    {
        private readonly object _lockObject;
        private bool _lockTaken;

        public BatchWrite(object lockObject)
        {
            _lockObject = lockObject;
            Monitor.Enter(_lockObject, ref _lockTaken);
        }
        
        public void Dispose()
        {
            if (!Disposed)
            {
                if (_lockTaken)
                {
                    _lockTaken = false;
                    Monitor.Exit(_lockObject);
                }

                Disposed = true;
            }
        }

        public bool Disposed { get; private set; }
    }
}
