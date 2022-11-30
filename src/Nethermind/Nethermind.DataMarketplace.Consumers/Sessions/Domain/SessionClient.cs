// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            return Equals((SessionClient)obj);
        }

        public override int GetHashCode()
        {
            return (Id != null ? Id.GetHashCode() : 0);
        }
    }
}
