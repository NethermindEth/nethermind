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

namespace Nethermind.DataMarketplace.Consumers.Sessions.Domain
{
    public class SessionClient : IEquatable<SessionClient>
    {
        private int _streamEnabled;
        private string?[] _args;
        public string Id { get; }

        public bool StreamEnabled
        {
            get => _streamEnabled == 1;
            private set => _streamEnabled = value ? 1 : 0;
        }

        public string?[] Args
        {
            get => _args;
            private set => _args = value;
        }

        public SessionClient(string id, bool streamEnabled = false, string?[]? args = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Invalid session client id.") : id;
            _args = args ?? Array.Empty<string>();
            if (streamEnabled)
            {
                EnableStream(_args);
            }
        }

        public void EnableStream(string?[] args)
        {
            Interlocked.Exchange(ref _streamEnabled, 1);
            Interlocked.Exchange(ref _args, args);
        }

        public void DisableStream()
        {
            Interlocked.Exchange(ref _streamEnabled, 0);
        }

        public bool Equals(SessionClient other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Id == other.Id;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((SessionClient) obj);
        }

        public override int GetHashCode()
        {
            return (Id != null ? Id.GetHashCode() : 0);
        }
    }
}