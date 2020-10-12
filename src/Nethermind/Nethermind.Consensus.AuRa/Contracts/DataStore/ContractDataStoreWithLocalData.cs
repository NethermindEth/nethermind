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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Processing;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class ContractDataStoreWithLocalData<T, TCollection> : ContractDataStore<T, TCollection> where TCollection : IContractDataStoreCollection<T>
    {
        private readonly ILocalDataSource<IEnumerable<T>> _localDataSource;
        private T[] _localData = Array.Empty<T>();

        protected internal ContractDataStoreWithLocalData(
            TCollection collection, 
            IDataContract<T> dataContract, 
            IBlockProcessor blockProcessor,
            ILogManager logManager,
            ILocalDataSource<IEnumerable<T>> localDataSource) 
            : base(collection, dataContract, blockProcessor, logManager)
        {
            _localDataSource = localDataSource ?? throw new ArgumentNullException(nameof(localDataSource));
            _localDataSource.Changed += OnChanged;
            LoadLocalData();
        }

        public event EventHandler Loaded;
        
        private void OnChanged(object sender, EventArgs e)
        {
            LoadLocalData();
            Loaded?.Invoke(this, EventArgs.Empty);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void LoadLocalData()
        {
            var oldData = _localData;
            _localData = _localDataSource.Data?.ToArray() ?? Array.Empty<T>();
            Collection.Remove(oldData.Except(_localData));
            Collection.Insert(_localData.Except(oldData));
        }

        protected override void RemoveOldContractItemsFromCollection()
        {
            Collection.Clear();
            Collection.Insert(_localData);
        }

        public override void Dispose()
        {
            base.Dispose();
            _localDataSource.Changed -= OnChanged;
        }
    }

    public class ContractDataStoreWithLocalData<T> : ContractDataStoreWithLocalData<T, IContractDataStoreCollection<T>>
    {
        public ContractDataStoreWithLocalData(
            IContractDataStoreCollection<T> collection, 
            IDataContract<T> dataContract, 
            IBlockProcessor blockProcessor,
            ILogManager logManager,
            ILocalDataSource<IEnumerable<T>> localDataSource) 
            : base(collection, dataContract ?? new EmptyDataContract<T>(), blockProcessor, logManager, localDataSource)
        {
        }
    }
}
