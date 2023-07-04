// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Blockchain.Data;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial class TxPriorityContract
    {
        public class LocalDataSource : FileLocalDataSource<LocalData>
        {
            public LocalDataSource(string filePath, IJsonSerializer jsonSerializer, IFileSystem fileSystem, ILogManager logManager, int interval = 500)
                : base(filePath, jsonSerializer, fileSystem, logManager, interval)
            {
            }

            public ILocalDataSource<IEnumerable<Address>> GetWhitelistLocalDataSource() => new LocalDataSource<Address>(this, LocalData.GetWhitelist);
            public ILocalDataSource<IEnumerable<Destination>> GetPrioritiesLocalDataSource() => new LocalDataSource<Destination>(this, LocalData.GetPriorities);
            public ILocalDataSource<IEnumerable<Destination>> GetMinGasPricesLocalDataSource() => new LocalDataSource<Destination>(this, LocalData.GetMinGasPrices);
            protected override LocalData DefaultValue => new LocalData();
        }

        private class LocalDataSource<T> : ILocalDataSource<IEnumerable<T>>
        {
            private readonly LocalDataSource _localDataSource;
            private readonly Func<LocalData, IEnumerable<T>> _getData;

            internal LocalDataSource(LocalDataSource localDataSource, Func<LocalData, IEnumerable<T>> getData)
            {
                _localDataSource = localDataSource;
                _getData = getData;
            }

            public IEnumerable<T> Data => _localDataSource.Data is null
                ? Enumerable.Empty<T>()
                : _getData(_localDataSource.Data) ?? Enumerable.Empty<T>();

            public event EventHandler Changed
            {
                add { _localDataSource.Changed += value; }
                remove { _localDataSource.Changed -= value; }
            }
        }

        public class LocalData
        {
            private Address[] _whitelist = Array.Empty<Address>();
            private Destination[] _priorities = Array.Empty<Destination>();
            private Destination[] _minGasPrices = Array.Empty<Destination>();

            public Address[] Whitelist
            {
                get => _whitelist;
                set => _whitelist = value ?? Array.Empty<Address>();
            }

            public Destination[] Priorities
            {
                get => _priorities;
                set => _priorities = (value ?? Array.Empty<Destination>()).Select(
                    d => new Destination(d.Target, d.FnSignature, d.Value, DestinationSource.Local)).ToArray();
            }

            public Destination[] MinGasPrices
            {
                get => _minGasPrices;
                set
                {
                    _minGasPrices = (value ?? Array.Empty<Destination>()).Select(
                        d => new Destination(d.Target, d.FnSignature, d.Value, DestinationSource.Local)).ToArray();
                }
            }

            internal static Address[] GetWhitelist(LocalData localData) => localData.Whitelist;
            internal static Destination[] GetPriorities(LocalData localData) => localData.Priorities;
            internal static Destination[] GetMinGasPrices(LocalData localData) => localData.MinGasPrices;
        }
    }
}
