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

using Nethermind.Core.Extensions;

namespace Nethermind.Db.Config
{
    public class DbConfig : IDbConfig
    {
        public static DbConfig Default = new DbConfig();

        public ulong WriteBufferSize { get; set; } = 16.MB();
        public uint WriteBufferNumber { get; set; } = 4;
        public ulong BlockCacheSize { get; set; } = 64.MB();
        public bool CacheIndexAndFilterBlocks { get; set; } = true;

        public ulong TraceDbWriteBufferSize { get; set; } = 256.MB();
        public uint TraceDbWriteBufferNumber { get; set; } = 4;
        public ulong TraceDbBlockCacheSize { get; set; } = 1024.MB();
        public bool TraceDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ReceiptsDbWriteBufferSize { get; set; } = 8.MB();
        public uint ReceiptsDbWriteBufferNumber { get; set; } = 4;
        public ulong ReceiptsDbBlockCacheSize { get; set; } = 32.MB();
        public bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong BlocksDbWriteBufferSize { get; set; } = 8.MB();
        public uint BlocksDbWriteBufferNumber { get; set; } = 4;
        public ulong BlocksDbBlockCacheSize { get; set; } = 32.MB();
        public bool BlocksDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong HeadersDbWriteBufferSize { get; set; } = 8.MB();
        public uint HeadersDbWriteBufferNumber { get; set; } = 4;
        public ulong HeadersDbBlockCacheSize { get; set; } = 32.MB();
        public bool HeadersDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong BlockInfosDbWriteBufferSize { get; set; } = 8.MB();
        public uint BlockInfosDbWriteBufferNumber { get; set; } = 4;
        public ulong BlockInfosDbBlockCacheSize { get; set; } = 32.MB();
        public bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; } = false;

        public ulong PendingTxsDbWriteBufferSize { get; set; } = 4.MB();
        public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
        public ulong PendingTxsDbBlockCacheSize { get; set; } = 16.MB();
        public bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong CodeDbWriteBufferSize { get; set; } = 2.MB();
        public uint CodeDbWriteBufferNumber { get; set; } = 4;
        public ulong CodeDbBlockCacheSize { get; set; } = 8.MB();
        public bool CodeDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong DepositsDbWriteBufferSize { get; set; } = 16.MB();
        public uint DepositsDbWriteBufferNumber { get; set; } = 4;
        public ulong DepositsDbBlockCacheSize { get; set; } = 64.MB();
        public bool DepositsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ConsumerSessionsDbWriteBufferSize { get; set; } = 16.MB();
        public uint ConsumerSessionsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConsumerSessionsDbBlockCacheSize { get; set; } = 64.MB();
        public bool ConsumerSessionsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ConsumerReceiptsDbWriteBufferSize { get; set; } = 16.MB();
        public uint ConsumerReceiptsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConsumerReceiptsDbBlockCacheSize { get; set; } = 64.MB();
        public bool ConsumerReceiptsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ConsumerDepositApprovalsDbWriteBufferSize { get; set; } = 16.MB();
        public uint ConsumerDepositApprovalsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConsumerDepositApprovalsDbBlockCacheSize { get; set; } = 64.MB();
        public bool ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ConfigsDbWriteBufferSize { get; set; } = 16.MB();
        public uint ConfigsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConfigsDbBlockCacheSize { get; set; } = 64.MB();
        public bool ConfigsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong EthRequestsDbWriteBufferSize { get; set; } = 16.MB();
        public uint EthRequestsDbWriteBufferNumber { get; set; } = 4;
        public ulong EthRequestsDbBlockCacheSize { get; set; } = 64.MB();
        public bool EthRequestsDbCacheIndexAndFilterBlocks { get; set; } = true;
        public uint RecycleLogFileNum { get; set; } = 0;
        public bool WriteAheadLogSync { get; set; } = false;
    }
}