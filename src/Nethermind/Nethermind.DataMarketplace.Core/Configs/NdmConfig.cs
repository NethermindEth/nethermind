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
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Configs
{
    public class NdmConfig : INdmConfig
    {
        public bool Enabled { get; set; }
        public string InitializerName { get; set; } = "ndm";
        public bool StoreConfigInDatabase { get; set; } = true;
        public string Id { get; set; } = "ndm";
        public string FilesPath { get; set; } = "ndm/files";
        public ulong FileMaxSize { get; set; } = 1024UL * 1024UL * 64UL;
        public string ProviderName { get; set; } = "Nethermind";
        public string Persistence { get; set; } = "rocks";
        public bool VerifyP2PSignature { get; set; } = true;
        public string? ProviderAddress { get; set; }
        public string? ProviderColdWalletAddress { get; set; }
        public string? ConsumerAddress { get; set; }
        public string? ContractAddress { get; set; } = "0x82c839fa4a41e158f613ec8a1a84be3c816d370f";
        public UInt256 ReceiptRequestThreshold { get; set; } = 10000000000000000;
        public UInt256 ReceiptsMergeThreshold { get; set; } = 100000000000000000;
        public UInt256 PaymentClaimThreshold { get; set; } = 1000000000000000000;
        public uint BlockConfirmations { get; set; }
        public bool FaucetEnabled { get; set; }
        public string? FaucetAddress { get; set; }
        public string? FaucetHost { get; set; }
        public UInt256 FaucetWeiRequestMaxValue { get; set; } = 1000000000000000000;
        public UInt256 FaucetEthDailyRequestsTotalValue { get; set; } = 500;
        public string PluginsPath { get; set; } = "ndm/plugins";
        public string DatabasePath { get; set; } = "ndm";
        public bool ProxyEnabled { get; set; }
        public string[] JsonRpcUrlProxies { get; set; } = Array.Empty<string>();
        public UInt256 GasPrice { get; set; } = 20000000000;
        public string GasPriceType { get; set; } = "custom";
        public uint CancelTransactionGasPricePercentageMultiplier { get; set; } = 110;
        public bool JsonRpcDataChannelEnabled { get; set; }
        public UInt256 RefundGasPrice { get; set; } = 20000000000;
        public UInt256 PaymentClaimGasPrice { get; set; } = 20000000000;

        public ulong DepositsDbWriteBufferSize { get; set; } = (ulong)16.MiB();
        public uint DepositsDbWriteBufferNumber { get; set; } = 4;
        public ulong DepositsDbBlockCacheSize { get; set; } = (ulong)64.MiB();
        public bool DepositsDbCacheIndexAndFilterBlocks { get; set; } = false;

        public ulong ConsumerSessionsDbWriteBufferSize { get; set; } = (ulong)16.MiB();
        public uint ConsumerSessionsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConsumerSessionsDbBlockCacheSize { get; set; } = (ulong)64.MiB();
        public bool ConsumerSessionsDbCacheIndexAndFilterBlocks { get; set; } = false;

        public ulong ConsumerReceiptsDbWriteBufferSize { get; set; } = (ulong)16.MiB();
        public uint ConsumerReceiptsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConsumerReceiptsDbBlockCacheSize { get; set; } = (ulong)64.MiB();
        public bool ConsumerReceiptsDbCacheIndexAndFilterBlocks { get; set; } = false;

        public ulong ConsumerDepositApprovalsDbWriteBufferSize { get; set; } = (ulong)16.MiB();
        public uint ConsumerDepositApprovalsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConsumerDepositApprovalsDbBlockCacheSize { get; set; } = (ulong)64.MiB();
        public bool ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks { get; set; } = false;

        public ulong ConfigsDbWriteBufferSize { get; set; } = (ulong)2.MiB();
        public uint ConfigsDbWriteBufferNumber { get; set; } = 4;
        public ulong ConfigsDbBlockCacheSize { get; set; } = (ulong)8.MiB();
        public bool ConfigsDbCacheIndexAndFilterBlocks { get; set; } = false;

        public ulong EthRequestsDbWriteBufferSize { get; set; } = (ulong)2.MiB();
        public uint EthRequestsDbWriteBufferNumber { get; set; } = 4;
        public ulong EthRequestsDbBlockCacheSize { get; set; } = (ulong)8.MiB();
        public bool EthRequestsDbCacheIndexAndFilterBlocks { get; set; } = false;
    }
}
