//  Copyright (c) 2022 Demerzel Solutions Limited
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
using System.Threading.Tasks;

namespace Nethermind.Core.MessageBus
{
    public class BusSubscription<T> : IBusSubcription where T : IMessage
    {
        private bool _disposed;
        private readonly Func<T, Task> _action;
        public event EventHandler? Disposed;

        public BusSubscription(Func<T, Task> action)
        {
            _action = action;
        }

        public Task Process(IMessage message)
        {
            if (!_disposed)
                return _action((T)message);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (!_disposed)
                Disposed?.Invoke(this, EventArgs.Empty);
            _disposed = true;
        }
    }
}
