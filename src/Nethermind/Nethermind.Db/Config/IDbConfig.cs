/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General License for more details.
 *
 * You should have received a copy of the GNU Lesser General License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Config;

namespace Nethermind.Db.Config
{
    public interface IDbConfig : IConfig
    {
        ulong WriteBufferSize { get; set; }
        uint WriteBufferNumber { get; set; }
        ulong BlockCacheSize { get; set; }
        bool CacheIndexAndFilterBlocks { get; set; }

        ulong TraceDbWriteBufferSize { get; set; }
        uint TraceDbWriteBufferNumber { get; set; }
        ulong TraceDbBlockCacheSize { get; set; }
        bool TraceDbCacheIndexAndFilterBlocks { get; set; }

        ulong ReceiptsDbWriteBufferSize { get; set; }
        uint ReceiptsDbWriteBufferNumber { get; set; }
        ulong ReceiptsDbBlockCacheSize { get; set; }
        bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; }

        ulong BlocksDbWriteBufferSize { get; set; }
        uint BlocksDbWriteBufferNumber { get; set; }
        ulong BlocksDbBlockCacheSize { get; set; }
        bool BlocksDbCacheIndexAndFilterBlocks { get; set; }

        ulong HeadersDbWriteBufferSize { get; set; }
        uint HeadersDbWriteBufferNumber { get; set; }
        ulong HeadersDbBlockCacheSize { get; set; }
        bool HeadersDbCacheIndexAndFilterBlocks { get; set; }

        ulong BlockInfosDbWriteBufferSize { get; set; }
        uint BlockInfosDbWriteBufferNumber { get; set; }
        ulong BlockInfosDbBlockCacheSize { get; set; }
        bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; }

        ulong PendingTxsDbWriteBufferSize { get; set; }
        uint PendingTxsDbWriteBufferNumber { get; set; }
        ulong PendingTxsDbBlockCacheSize { get; set; }
        bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; }

        ulong CodeDbWriteBufferSize { get; set; }
        uint CodeDbWriteBufferNumber { get; set; }
        ulong CodeDbBlockCacheSize { get; set; }
        bool CodeDbCacheIndexAndFilterBlocks { get; set; }

        ulong DepositsDbWriteBufferSize { get; set; }
        uint DepositsDbWriteBufferNumber { get; set; }
        ulong DepositsDbBlockCacheSize { get; set; }
        bool DepositsDbCacheIndexAndFilterBlocks { get; set; }

        ulong ConsumerSessionsDbWriteBufferSize { get; set; }
        uint ConsumerSessionsDbWriteBufferNumber { get; set; }
        ulong ConsumerSessionsDbBlockCacheSize { get; set; }
        bool ConsumerSessionsDbCacheIndexAndFilterBlocks { get; set; }

        ulong ConsumerReceiptsDbWriteBufferSize { get; set; }
        uint ConsumerReceiptsDbWriteBufferNumber { get; set; }
        ulong ConsumerReceiptsDbBlockCacheSize { get; set; }
        bool ConsumerReceiptsDbCacheIndexAndFilterBlocks { get; set; }

        ulong ConsumerDepositApprovalsDbWriteBufferSize { get; set; }
        uint ConsumerDepositApprovalsDbWriteBufferNumber { get; set; }
        ulong ConsumerDepositApprovalsDbBlockCacheSize { get; set; }
        bool ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks { get; set; }

        ulong ConfigsDbWriteBufferSize { get; set; }
        uint ConfigsDbWriteBufferNumber { get; set; }
        ulong ConfigsDbBlockCacheSize { get; set; }
        bool ConfigsDbCacheIndexAndFilterBlocks { get; set; }
        
        ulong EthRequestsDbWriteBufferSize { get; set; }
        uint EthRequestsDbWriteBufferNumber { get; set; }
        ulong EthRequestsDbBlockCacheSize { get; set; }
        bool EthRequestsDbCacheIndexAndFilterBlocks { get; set; }
        
        uint RecycleLogFileNum { get; set; }
        bool WriteAheadLogSync { get; set; }
        
    }
}