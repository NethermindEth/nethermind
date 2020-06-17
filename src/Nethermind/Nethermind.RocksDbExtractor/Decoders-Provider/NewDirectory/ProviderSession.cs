/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by  
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class ProviderSession : Session, IEquatable<ProviderSession>
    {
        private long _graceUnits;
        private int _dataAvailability;
        private readonly ConcurrentDictionary<string, SessionClient>
            _clients = new ConcurrentDictionary<string, SessionClient>();

        public DataAvailability DataAvailability
        {
            get => (DataAvailability) _dataAvailability;
            private set => _dataAvailability = (int) value;
        }
        
        public IEnumerable<SessionClient> Clients => _clients.Values;

        public SessionClient? GetClient(string client)
            => _clients.TryGetValue(client, out SessionClient? sessionClient) ? sessionClient : null;
        
        public uint GraceUnits
        {
            get => (uint) _graceUnits;
            private set => _graceUnits = value;
        }

        public ProviderSession(Keccak id, Keccak depositId, Keccak dataAssetId, Address consumerAddress,
            PublicKey consumerNodeId, Address providerAddress, PublicKey providerNodeId, uint startUnitsFromConsumer,
            uint startUnitsFromProvider) : this(id, depositId, dataAssetId, consumerAddress, consumerNodeId,
            providerAddress, providerNodeId, SessionState.Unknown, startUnitsFromConsumer, startUnitsFromProvider)
        {
        }

        public ProviderSession(Keccak id, Keccak depositId, Keccak dataAssetId, Address consumerAddress,
            PublicKey consumerNodeId, Address providerAddress, PublicKey providerNodeId, SessionState state,
            uint startUnitsFromConsumer, uint startUnitsFromProvider, ulong startTimestamp = 0,
            ulong finishTimestamp = 0, uint consumedUnits = 0, uint unpaidUnits = 0, uint paidUnits = 0,
            uint settledUnits = 0, uint graceUnits = 0, DataAvailability dataAvailability = DataAvailability.Unknown):
            base(id, depositId, dataAssetId, consumerAddress, consumerNodeId, providerAddress, providerNodeId, state,
                startUnitsFromConsumer, startUnitsFromProvider, startTimestamp, finishTimestamp, consumedUnits,
                unpaidUnits, paidUnits, settledUnits)
        {
            _graceUnits = graceUnits;
            _dataAvailability = (int) dataAvailability;
        }

        public void Start(ulong timestamp)
        {
            State = SessionState.Started;
            StartTimestamp = timestamp;
            SetDataAvailability(DataAvailability.Available);
        }

        public void Finish(SessionState state, ulong timestamp)
        {
            if (State == state)
            {
                return;
            }

            ValidateIfSessionStarted();
            if (state == SessionState.Unknown || state == SessionState.Started)
            {
                throw new InvalidOperationException($"Session: '{Id}' cannot be finished, invalid state: '{state}'.");
            }

            State = state;
            FinishTimestamp = timestamp;
            foreach (var (_, client) in _clients)
            {
                client.DisableStream();
            }

            _clients.Clear();
        }

        public void SetDataAvailability(DataAvailability dataAvailability)
            => Interlocked.Exchange(ref _dataAvailability, (int) dataAvailability);

        public bool IsDataStreamEnabled(string client)
            => _clients.TryGetValue(client, out var sessionClient) && sessionClient.StreamEnabled;

        public void EnableStream(string client, string[] args)
        {
            if (string.IsNullOrWhiteSpace(client))
            {
                throw new ArgumentException("Invalid session client id.", nameof(client));
            }
            
            ValidateIfSessionStarted();
            _clients.AddOrUpdate(client,
                _ => new SessionClient(client, true, args),
                (_, session) =>
                {
                    session.EnableStream(args);

                    return session;
                });
        }

        public void DisableStream(string client)
        {
            if (string.IsNullOrWhiteSpace(client))
            {
                throw new ArgumentException("Invalid session client id.", nameof(client));
            }

            if (!_clients.TryGetValue(client, out var sessionClient))
            {
                return;
            }

            sessionClient.DisableStream();
        }

        public void IncrementConsumedUnits()
        {
            ValidateIfSessionStarted();
            Interlocked.Increment(ref _consumedUnits);
        }

        public void SetConsumedUnits(uint units)
        {
            ValidateIfSessionStarted();
            Interlocked.Exchange(ref _consumedUnits, units);
        }

        public void IncrementUnpaidUnits()
        {
            ValidateIfSessionStarted();
            Interlocked.Increment(ref _unpaidUnits);
        }

        public void SetUnpaidUnits(uint units)
        {
            ValidateIfSessionStarted();
            Interlocked.Exchange(ref _unpaidUnits, units);
        }

        public void AddUnpaidUnits(uint units)
        {
            ValidateIfSessionStarted();
            Interlocked.Add(ref _unpaidUnits, units);
        }
        
        public void SubtractUnpaidUnits(uint units)
        {
            ValidateIfSessionStarted();
            Interlocked.Add(ref _unpaidUnits, -1 * units);
        }

        public void AddPaidUnits(uint units)
        {
            ValidateIfSessionStarted();
            Interlocked.Add(ref _paidUnits, units);
        }

        public void SetPaidUnits(uint units)
        {
            ValidateIfSessionStarted();
            Interlocked.Exchange(ref _paidUnits, units);
        }

        public void SettleUnits(uint units)
        {
            ValidateIfSessionStarted();
            if (_settledUnits > 0)
            {
                return;
            }

            Interlocked.Exchange(ref _settledUnits, units);
        }
        
        public void AddGraceUnits(uint units)
        {
            ValidateIfSessionStarted();
            Interlocked.Add(ref _graceUnits, units);
        }

        private void ValidateIfSessionStarted()
        {
            if (State != SessionState.Started)
            {
                throw new InvalidOperationException($"Session: '{Id}' was not started, current state: '{State}'.");
            }
        }

        public bool Equals(ProviderSession other) => base.Equals(other);
    }
}