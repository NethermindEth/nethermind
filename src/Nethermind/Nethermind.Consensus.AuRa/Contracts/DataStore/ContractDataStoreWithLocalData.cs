// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
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
