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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class ContractDataStoreWithLocalData<T> : ContractDataStore<T>
    {
        private readonly ILocalDataSource<IEnumerable<T>> _localDataSource;
        private T[] _localData = Array.Empty<T>();

        public ContractDataStoreWithLocalData(
            IContractDataStoreCollection<T> collection, 
            IDataContract<T> dataContract, 
            IBlockTree blockTree, 
            IReceiptFinder receiptFinder,
            ILogManager logManager,
            ILocalDataSource<IEnumerable<T>> localDataSource) 
            : base(collection, dataContract ?? new EmptyDataContract<T>(), blockTree, receiptFinder, logManager)
        {
            _localDataSource = localDataSource ?? throw new ArgumentNullException(nameof(localDataSource));
            _localDataSource.Changed += OnChanged;
            LoadLocalData();
        }

        public event EventHandler Loaded;
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        private void OnChanged(object sender, EventArgs e)
        {
            LoadLocalData();
            Thread.MemoryBarrier();
            Loaded?.Invoke(this, EventArgs.Empty);
        }

        private void LoadLocalData()
        {
            TraceDataChanged("local data start");
            Collection.Remove(_localData);
            TraceDataChanged("local data removed");
            _localData = _localDataSource.Data?.ToArray() ?? Array.Empty<T>();
            Collection.Insert(_localData, true);
            TraceDataChanged("local data inserted");
        }

        protected override void RemoveOldContractItemsFromCollection()
        {
            base.RemoveOldContractItemsFromCollection();
            Collection.Insert(_localData);
        }

        public override void Dispose()
        {
            base.Dispose();
            _localDataSource.Changed -= OnChanged;
        }
    }
}
