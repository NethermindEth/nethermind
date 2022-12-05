// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Attributes;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Configs
{
    public interface INdmConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then it disables the NDM (Nethermind Data Marketplace) capability", DefaultValue = "false")]
        bool Enabled { get; }
        [ConfigItem(Description = "Type of the initializer that will be used to bootstrap NDM", DefaultValue = "ndm")]
        string InitializerName { get; }
        [ConfigItem(Description = "If 'false' then it reads the configuration from file, instead of the database", DefaultValue = "true")]
        bool StoreConfigInDatabase { get; }
        [ConfigItem(Description = "An arbitrary ID of the configuration that will be stored in a database", DefaultValue = "ndm")]
        string Id { get; }
        [ConfigItem(Description = "Path to the directory where NDM files (e.g. data provider assets such as documents) will be kept", DefaultValue = "ndm/files")]
        string FilesPath { get; }
        [ConfigItem(Description = "Max file size in bytes of a single data provider asset file", DefaultValue = "67108864")]
        ulong FileMaxSize { get; }
        [ConfigItem(Description = "An arbitrary name of the data provider", DefaultValue = "Nethermind")]
        string ProviderName { get; }
        [ConfigItem(Description = "Type of the database provider, possible values: rocks, mongo", DefaultValue = "rocks")]
        string Persistence { get; }
        [ConfigItem(Description = "If 'false' then signature verification will be skipped during NDM capability P2P handshake", DefaultValue = "true")]
        bool VerifyP2PSignature { get; }
        [ConfigItem(Description = "An account address (hot wallet) of the data provider", DefaultValue = null)]
        string? ProviderAddress { get; }
        [ConfigItem(Description = "An account address (cold wallet) of the data provider", DefaultValue = null)]
        string? ProviderColdWalletAddress { get; }
        [ConfigItem(Description = "An account address (hot wallet) of the data consumer", DefaultValue = null)]
        string? ConsumerAddress { get; }

        [DoNotUseInSecuredContext("Hardcode so cannot be overwritten to redirect to another contract")]
        [ConfigItem(Description = "An address of the smart contract used by NDM", DefaultValue = "0x82c839fa4a41e158f613ec8a1a84be3c816d370f")]
        string? ContractAddress { get; }

        [ConfigItem(Description = "Data provider's threshold (Wei) that once reached will send a receipt request to the data consumer", DefaultValue = "10000000000000000")]
        UInt256 ReceiptRequestThreshold { get; }
        [ConfigItem(Description = "Data provider's threshold (Wei) that once reached will send a merge receipts request to the data consumer", DefaultValue = "100000000000000000")]
        UInt256 ReceiptsMergeThreshold { get; }
        [ConfigItem(Description = "Data provider's threshold (Wei) that once reached will send a payment claim to the data consumer", DefaultValue = "1000000000000000000")]
        UInt256 PaymentClaimThreshold { get; }
        [ConfigItem(Description = "Number of the required blocks confirmations (e.g. 6) to mark the transaction as successfully processed", DefaultValue = "0")]
        uint BlockConfirmations { get; }
        [ConfigItem(Description = "If 'true' then it enables the faucet capability", DefaultValue = "false")]
        bool FaucetEnabled { get; }
        [ConfigItem(Description = "An account address that will be used to transfer the funds from if faucet capability is enabled", DefaultValue = null)]
        string? FaucetAddress { get; }
        [ConfigItem(Description = "IP address of the faucet to connect to in order to request ETH", DefaultValue = null)]
        string? FaucetHost { get; }
        [ConfigItem(Description = "Maximal value (Wei) of a single ETH request to the faucet", DefaultValue = "1000000000000000000")]
        UInt256 FaucetWeiRequestMaxValue { get; }
        [ConfigItem(Description = "Maximal value (ETH) of a total ETH requests (per day) to the faucet", DefaultValue = "500")]
        UInt256 FaucetEthDailyRequestsTotalValue { get; }
        [ConfigItem(Description = "An arbitrary path to the plugins directory that should be loaded as external assemblies", DefaultValue = "ndm/plugins")]
        string PluginsPath { get; }
        [ConfigItem(Description = "Path to the directory, where NDM databases will be kept, when using RocksDB provider", DefaultValue = "ndm")]
        string DatabasePath { get; }
        [ConfigItem(Description = "If 'true' then JSON RPC calls will be redirected to the specified proxies.", DefaultValue = "false")]
        bool ProxyEnabled { get; }
        [ConfigItem(Description = "'List of JSON RPC URLs proxies.", DefaultValue = "System.String[]")]
        string[] JsonRpcUrlProxies { get; }
        [ConfigItem(Description = "Gas price (make deposit, claim payment etc.).", DefaultValue = "20000000000")]
        UInt256 GasPrice { get; }
        [ConfigItem(Description = "Gas price type ('custom', 'safeLow', 'average', 'fast', 'fastest').", DefaultValue = "custom")]
        string GasPriceType { get; }
        [ConfigItem(Description = "Percentage multiplier (by default 110%) for calculating gas price when canceling transaction.", DefaultValue = "110")]
        uint CancelTransactionGasPricePercentageMultiplier { get; set; }
        [ConfigItem(Description = "If 'true', data stream results can be fetched via 'ndm_pullData('depositId')' method.", DefaultValue = "false")]
        bool JsonRpcDataChannelEnabled { get; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong DepositsDbWriteBufferSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        uint DepositsDbWriteBufferNumber { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong DepositsDbBlockCacheSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        bool DepositsDbCacheIndexAndFilterBlocks { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConsumerSessionsDbWriteBufferSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        uint ConsumerSessionsDbWriteBufferNumber { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConsumerSessionsDbBlockCacheSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        bool ConsumerSessionsDbCacheIndexAndFilterBlocks { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConsumerReceiptsDbWriteBufferSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        uint ConsumerReceiptsDbWriteBufferNumber { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConsumerReceiptsDbBlockCacheSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        bool ConsumerReceiptsDbCacheIndexAndFilterBlocks { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConsumerDepositApprovalsDbWriteBufferSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        uint ConsumerDepositApprovalsDbWriteBufferNumber { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConsumerDepositApprovalsDbBlockCacheSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        bool ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConfigsDbWriteBufferSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        uint ConfigsDbWriteBufferNumber { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong ConfigsDbBlockCacheSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        bool ConfigsDbCacheIndexAndFilterBlocks { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong EthRequestsDbWriteBufferSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        uint EthRequestsDbWriteBufferNumber { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        ulong EthRequestsDbBlockCacheSize { get; set; }
        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        bool EthRequestsDbCacheIndexAndFilterBlocks { get; set; }
    }
}
