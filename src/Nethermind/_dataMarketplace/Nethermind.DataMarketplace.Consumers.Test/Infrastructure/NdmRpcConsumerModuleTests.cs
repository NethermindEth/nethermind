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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Consumers.Shared.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure
{
    public class NdmRpcConsumerModuleTests
    {
        private IConsumerService _consumerService;
        private IDepositReportService _depositReportService;
        private IJsonRpcNdmConsumerChannel _jsonRpcNdmConsumerChannel;
        private IEthRequestService _ethRequestService;
        private IPriceService _priceService;
        private IGasPriceService _gasPriceService;
        private IConsumerTransactionsService _consumerTransactionsService;
        private IConsumerGasLimitsService _gasLimitsService;
        private IWallet _wallet;
        private INdmRpcConsumerModule _rpc;
        private ITimestamper _timestamper;
        private const uint DepositExpiryTime = 1546393600;
        private static readonly DateTime Date = new DateTime(2019, 1, 2); //1546383600

        [SetUp]
        public void Setup()
        {
            _consumerService = Substitute.For<IConsumerService>();
            _depositReportService = Substitute.For<IDepositReportService>();
            _jsonRpcNdmConsumerChannel = Substitute.For<IJsonRpcNdmConsumerChannel>();
            _ethRequestService = Substitute.For<IEthRequestService>();
            _priceService = Substitute.For<IPriceService>();
            _gasPriceService = Substitute.For<IGasPriceService>();
            _gasLimitsService = Substitute.For<IConsumerGasLimitsService>();
            _consumerTransactionsService = Substitute.For<IConsumerTransactionsService>();
            _wallet = Substitute.For<IWallet>();
            _timestamper = new Timestamper(Date);
            _rpc = new NdmRpcConsumerModule(_consumerService, _depositReportService, _jsonRpcNdmConsumerChannel,
                _ethRequestService, _gasPriceService, _consumerTransactionsService, _gasLimitsService,
                _wallet, _timestamper, _priceService);
        }

        [Test]
        public void module_type_should_be_ndm_consumer()
        {
             typeof(INdmRpcConsumerModule).GetCustomAttribute<RpcModuleAttribute>().ModuleType.Should().Be(ModuleType.NdmConsumer);
        }

        [Test]
        public void given_wallet_list_accounts_should_return_accounts()
        {
            _wallet.GetAccounts().Returns(new[] {TestItem.AddressA});
            var result = _rpc.ndm_listAccounts();
            _wallet.Received().GetAccounts();
            result.Data.Should().ContainSingle();
            var account = result.Data.Single();
            account.Should().NotBeNull();
            account.Address.Should().NotBeNull();
            account.Unlocked.Should().BeFalse();
        }

        [Test]
        public void given_null_wallet_list_accounts_should_return_no_accounts()
        {
            _rpc = new NdmRpcConsumerModule(
                _consumerService,
                _depositReportService,
                _jsonRpcNdmConsumerChannel,
                _ethRequestService,
                _gasPriceService,
                _consumerTransactionsService,
                _gasLimitsService,
                NullWallet.Instance, 
                _timestamper,
                _priceService);
            var result = _rpc.ndm_listAccounts();
            result.Data.Should().BeEmpty();
        }

        [Test]
        public void get_consumer_address_should_return_address()
        {
            _consumerService.GetAddress().Returns(TestItem.AddressA);
            var result = _rpc.ndm_getConsumerAddress();
            _consumerService.Received().GetAddress();
            result.Data.Should().Be(TestItem.AddressA);
        }

        [Test]
        public async Task change_consumer_address_should_return_changed_address()
        {
            var result = await _rpc.ndm_changeConsumerAddress(TestItem.AddressA);
            await _consumerService.Received().ChangeAddressAsync(TestItem.AddressA);
            result.Data.Should().Be(TestItem.AddressA);
        }

        [Test]
        public void get_discovered_data_assets_should_return_data_assets()
        {
            _consumerService.GetDiscoveredDataAssets().Returns(new List<DataAsset> {GetDataAsset()});
            var result = _rpc.ndm_getDiscoveredDataAssets();
            _consumerService.Received().GetDiscoveredDataAssets();
            result.Data.Should().ContainSingle();
            VerifyDataAsset(result.Data.Single());
        }

        [Test]
        public async Task get_known_data_assets_should_return_data_asset_info()
        {
            _consumerService.GetKnownDataAssetsAsync()
                .Returns(new[] {new DataAssetInfo(Keccak.Zero, "test", "test")});
            var result = await _rpc.ndm_getKnownDataAssets();
            await _consumerService.Received().GetKnownDataAssetsAsync();
            result.Data.Should().ContainSingle();
            var dataAsset = result.Data.Single();
            dataAsset.Id.Should().Be(Keccak.Zero);
            dataAsset.Name.Should().Be("test");
            dataAsset.Description.Should().Be("test");
        }

        [Test]
        public async Task get_known_providers_should_return_providers_info()
        {
            _consumerService.GetKnownProvidersAsync().Returns(new[] {new ProviderInfo("test", TestItem.AddressA)});
            var result = await _rpc.ndm_getKnownProviders();
            await _consumerService.Received().GetKnownProvidersAsync();
            result.Data.Should().ContainSingle();
            var provider = result.Data.Single();
            provider.Name.Should().Be("test");
            provider.Address.Should().Be(TestItem.AddressA);
        }

        [Test]
        public void get_connected_providers_should_return_providers_addresses()
        {
            _consumerService.GetConnectedProviders().Returns(new[] {TestItem.AddressA});
            var result = _rpc.ndm_getConnectedProviders();
            _consumerService.Received().GetConnectedProviders();
            result.Data.Should().ContainSingle();
            result.Data.Single().Should().Be(TestItem.AddressA);
        }

        [Test]
        public void get_active_consumer_sessions_should_return_sessions()
        {
            _consumerService.GetActiveSessions().Returns(new[] {GetConsumerSession()});
            var result = _rpc.ndm_getActiveConsumerSessions();
            _consumerService.Received().GetActiveSessions();
            result.Data.Should().ContainSingle();
            var session = result.Data.Single();
            VerifyConsumerSession(session);
        }

        [Test]
        public async Task get_deposits_should_return_paged_results_of_deposits()
        {
            var query = new GetDeposits();
            _consumerService.GetDepositsAsync(query)
                .Returns(PagedResult<DepositDetails>.Create(new[] {GetDepositDetails()}, 1, 1, 1, 1));
            var result = await _rpc.ndm_getDeposits(query);
            await _consumerService.Received().GetDepositsAsync(query);
            result.Data.Items.Should().ContainSingle();
            result.Data.Page.Should().Be(1);
            result.Data.Results.Should().Be(1);
            result.Data.TotalPages.Should().Be(1);
            result.Data.TotalResults.Should().Be(1);
            result.Data.IsEmpty.Should().BeFalse();
            VerifyDepositDetails(result.Data.Items.Single());
        }

        [Test]
        public async Task get_deposit_should_return_deposit()
        {
            var depositId = TestItem.KeccakA;
            _consumerService.GetDepositAsync(depositId).Returns(GetDepositDetails());
            var result = await _rpc.ndm_getDeposit(depositId);
            await _consumerService.Received().GetDepositAsync(depositId);
            result.Result.ResultType.Should().Be(ResultType.Success);
            result.ErrorCode.Should().Be(ErrorCodes.None);
            result.Result.Error.Should().BeNullOrWhiteSpace();
            VerifyDepositDetails(result.Data);
        }

        [Test]
        public async Task get_deposit_should_fail_if_deposit_was_not_found()
        {
            var depositId = TestItem.KeccakA;
            var result = await _rpc.ndm_getDeposit(depositId);
            await _consumerService.Received().GetDepositAsync(depositId);
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InternalError);
            result.Result.Error.Should().NotBeNull();
        }

        [Test]
        public async Task make_deposit_should_return_deposit_id()
        {
            var request = new MakeDepositForRpc
            {
                DataAssetId = Keccak.Zero,
                Units = 10,
                Value = 100
            };
            var depositId = TestItem.KeccakA;
            _consumerService.MakeDepositAsync(request.DataAssetId, request.Units, request.Value).Returns(depositId);
            var result = await _rpc.ndm_makeDeposit(request);
            await _consumerService.Received().MakeDepositAsync(request.DataAssetId, request.Units, request.Value);
            result.Data.Should().Be(depositId);
        }

        [Test]
        public async Task send_data_request_should_return_result()
        {
            var depositId = TestItem.KeccakA;
            var dataRequestResult = DataRequestResult.DepositVerified;
            _consumerService.SendDataRequestAsync(depositId).Returns(dataRequestResult);
            var result = await _rpc.ndm_sendDataRequest(depositId);
            await _consumerService.Received().SendDataRequestAsync(depositId);
            result.Data.Should().Be(dataRequestResult.ToString());
        }

        [Test]
        public async Task finish_session_should_return_deposit_id()
        {
            var depositId = TestItem.KeccakA;
            _consumerService.SendFinishSessionAsync(depositId).Returns(depositId);
            var result = await _rpc.ndm_finishSession(depositId);
            await _consumerService.Received().SendFinishSessionAsync(depositId);
            result.Data.Should().Be(depositId);
        }

        [Test]
        public async Task finish_session_should_fail_if_deposit_was_not_found()
        {
            var depositId = TestItem.KeccakA;
            var result = await _rpc.ndm_finishSession(depositId);
            await _consumerService.Received().SendFinishSessionAsync(depositId);
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InternalError);
            result.Result.Error.Should().NotBeNull();
        }

        [Test]
        public async Task enable_data_stream_should_return_deposit_id()
        {
            var depositId = TestItem.KeccakA;
            var client = "client";
            var args = new[] {"test"};
            _consumerService.EnableDataStreamAsync(depositId, client, args).Returns(depositId);
            var result = await _rpc.ndm_enableDataStream(depositId, client, args);
            await _consumerService.Received().EnableDataStreamAsync(depositId, client, args);
            result.Data.Should().Be(depositId);
        }

        [Test]
        public async Task enable_data_stream_should_fail_if_deposit_was_not_found()
        {
            var depositId = TestItem.KeccakA;
            var client = "client";
            var args = new[] {"test"};
            var result = await _rpc.ndm_enableDataStream(depositId, client, args);
            await _consumerService.Received().EnableDataStreamAsync(depositId, client, args);
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InternalError);
            result.Result.Error.Should().NotBeNull();
        }

        [Test]
        public async Task disable_data_stream_should_return_deposit_id()
        {
            var depositId = TestItem.KeccakA;
            var client = "client";
            _consumerService.DisableDataStreamAsync(depositId, client).Returns(depositId);
            var result = await _rpc.ndm_disableDataStream(depositId, client);
            await _consumerService.Received().DisableDataStreamAsync(depositId, client);
            result.Data.Should().Be(depositId);
        }

        [Test]
        public async Task disable_data_stream_should_fail_if_deposit_was_not_found()
        {
            var depositId = TestItem.KeccakA;
            var client = "client";
            var result = await _rpc.ndm_disableDataStream(depositId, client);
            await _consumerService.Received().DisableDataStreamAsync(depositId, client);
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InternalError);
            result.Result.Error.Should().NotBeNull();
        }
        
        [Test]
        public async Task disable_data_streams_should_return_deposit_id()
        {
            var depositId = TestItem.KeccakA;
            _consumerService.DisableDataStreamsAsync(depositId).Returns(depositId);
            var result = await _rpc.ndm_disableDataStreams(depositId);
            await _consumerService.Received().DisableDataStreamsAsync(depositId);
            result.Data.Should().Be(depositId);
        }

        [Test]
        public async Task disable_data_streams_should_fail_if_deposit_was_not_found()
        {
            var depositId = TestItem.KeccakA;
            var result = await _rpc.ndm_disableDataStreams(depositId);
            await _consumerService.Received().DisableDataStreamsAsync(depositId);
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InternalError);
            result.Result.Error.Should().NotBeNull();
        }

        [Test]
        public async Task get_deposits_report_should_return_report()
        {
            var query = new GetDepositsReport();
            var item = GetDepositReportItem();
            var report = new DepositsReport(1, 1, 0,
                PagedResult<DepositReportItem>.Create(new[] {item}, 1, 1, 1, 1));
            _depositReportService.GetAsync(query).Returns(report);
            var result = await _rpc.ndm_getDepositsReport(query);
            await _depositReportService.Received().GetAsync(query);
            result.Data.Should().NotBeNull();
            result.Data.Deposits.Should().NotBeNull();
            result.Data.Deposits.Items.Should().ContainSingle();
            result.Data.Deposits.Page.Should().Be(1);
            result.Data.Deposits.Results.Should().Be(1);
            result.Data.Deposits.TotalPages.Should().Be(1);
            result.Data.Deposits.TotalResults.Should().Be(1);
            result.Data.Deposits.IsEmpty.Should().BeFalse();
            result.Data.ClaimedValue.Should().Be(1);
            result.Data.RefundedValue.Should().Be(0);
            result.Data.RemainingValue.Should().Be(0);
            result.Data.TotalValue.Should().Be(1);
            VerifyDepositReportItem(result.Data.Deposits.Items.Single(), item);
        }

        [Test]
        public async Task get_deposit_approvals_should_return_approvals()
        {
            var query = new GetConsumerDepositApprovals();
            var approval = new DepositApproval(TestItem.KeccakA, "test", "kyc",
                TestItem.AddressA, TestItem.AddressB, 1, DepositApprovalState.Pending);
            _consumerService.GetDepositApprovalsAsync(query)
                .Returns(PagedResult<DepositApproval>.Create(new[] {approval}, 1, 1, 1, 1));
            var result = await _rpc.ndm_getConsumerDepositApprovals(query);
            await _consumerService.Received().GetDepositApprovalsAsync(query);
            result.Data.Should().NotBeNull();
            result.Data.Items.Should().NotBeNull();
            result.Data.Items.Should().ContainSingle();
            result.Data.Page.Should().Be(1);
            result.Data.Results.Should().Be(1);
            result.Data.TotalPages.Should().Be(1);
            result.Data.TotalResults.Should().Be(1);
            result.Data.IsEmpty.Should().BeFalse();
            var rpcApproval = result.Data.Items.Single();
            rpcApproval.Id.Should().Be(approval.Id);
            rpcApproval.AssetId.Should().Be(approval.AssetId);
            rpcApproval.AssetName.Should().Be(approval.AssetName);
            rpcApproval.Kyc.Should().Be(approval.Kyc);
            rpcApproval.Consumer.Should().Be(approval.Consumer);
            rpcApproval.Provider.Should().Be(approval.Provider);
            rpcApproval.Timestamp.Should().Be(approval.Timestamp);
        }

        [Test]
        public async Task request_deposit_approval_should_return_id()
        {
            var id = TestItem.KeccakA;
            _consumerService.RequestDepositApprovalAsync(Keccak.Zero, "kyc").Returns(id);
            var result = await _rpc.ndm_requestDepositApproval(Keccak.Zero, "kyc");
            await _consumerService.Received().RequestDepositApprovalAsync(Keccak.Zero, "kyc");
            result.Data.Should().Be(id);
        }

        [Test]
        public async Task request_deposit_approval_should_fail_if_asset_was_not_found()
        {
            var result = await _rpc.ndm_requestDepositApproval(Keccak.Zero, "kyc");
            await _consumerService.Received().RequestDepositApprovalAsync(Keccak.Zero, "kyc");
            result.Data.Should().BeNull();
        }

        [Test]
        public async Task request_eth_should_return_true()
        {
            var address = TestItem.AddressA;
            UInt256 value = 1.Ether();
            _ethRequestService.TryRequestEthAsync(address, value).Returns(FaucetResponse.RequestCompleted(null));
            var result = await _rpc.ndm_requestEth(address);
            await _ethRequestService.Received().TryRequestEthAsync(address, value);
            result.Data.Status.Should().Be(FaucetRequestStatus.RequestCompleted.ToString());
        }

        [Test]
        public async Task request_eth_should_fail_if_request_cannot_be_made()
        {
            var address = TestItem.AddressA;
            var value = 1.Ether();
            _ethRequestService.TryRequestEthAsync(address, value).Returns(FaucetResponse.RequestError);
            var result = await _rpc.ndm_requestEth(address);
            await _ethRequestService.Received().TryRequestEthAsync(address, value);
            result.Data.Status.Should().Be(FaucetRequestStatus.RequestError.ToString());
        }

        [Test]
        public void pull_data_should_return_data()
        {
            var depositId = Keccak.Zero;
            const string data = "data";
            _jsonRpcNdmConsumerChannel.Pull(depositId).Returns(data);
            var result = _rpc.ndm_pullData(depositId);
            _jsonRpcNdmConsumerChannel.Received().Pull(depositId);
            result.Data.Should().Be(data);
        }
        
        [Test]
        public async Task get_proxy_should_return_proxy_info()
        {
            var ndmProxy = new NdmProxy(true, new[] {"http://localhost:8545"});
            _consumerService.GetProxyAsync().Returns(ndmProxy);
            var result = await _rpc.ndm_getProxy();
            result.Data.Enabled.Should().Be(ndmProxy.Enabled);
            result.Data.Urls.Should().BeSameAs(ndmProxy.Urls);
            await _consumerService.Received().GetProxyAsync();
        }
        
        [Test]
        public async Task set_proxy_should_return_true()
        {
            var urls = new[] {"http://localhost:8545"};
            var result = await _rpc.ndm_setProxy(urls);
            result.Data.Should().BeTrue();
            await _consumerService.Received().SetProxyAsync(urls);
        }

        [Test]
        public void get_eth_usd_price_should_return_amount()
        {
            const decimal price = 187;
            const ulong updatedAt = 123456789;
            _priceService.Get("USDT_ETH").Returns(new PriceInfo(price, updatedAt));
            var result = _rpc.ndm_getUsdPrice("USDT_ETH");
            result.Data.Price.Should().Be(price);
            result.Data.UpdatedAt.Should().Be(updatedAt);
        }

        [Test]
        public void get_gas_price_should_return_types()
        {
            const string type = "test";
            const ulong updatedAt = 123456789;
            var safeLow = new GasPriceDetails(1, 1000);
            var average = new GasPriceDetails(10, 100);
            var fast = new GasPriceDetails(100, 100);
            var fastest = new GasPriceDetails(1000, 1);
            var custom = new GasPriceDetails(500, 2);
            _gasPriceService.Types.Returns(new GasPriceTypes(safeLow, average, fast, fastest, custom, type, updatedAt));
            var result = _rpc.ndm_getGasPrice();
            VerifyGasPrice(result.Data.SafeLow, safeLow);
            VerifyGasPrice(result.Data.Average, average);
            VerifyGasPrice(result.Data.Fast, fast);
            VerifyGasPrice(result.Data.Fastest, fastest);
            VerifyGasPrice(result.Data.Custom, custom);
            result.Data.Type.Should().Be(type);
            result.Data.UpdatedAt.Should().Be(updatedAt);
        }

        [Test]
        public async Task set_gas_price_should_return_true()
        {
            const string type = "test";
            var result = await _rpc.ndm_setGasPrice(type);
            result.Data.Should().BeTrue();
            await _gasPriceService.Received().SetGasPriceOrTypeAsync(type);
        }
        
        [Test]
        public async Task update_deposit_gas_price_should_return_transaction_hash()
        {
            var depositId = TestItem.KeccakA;
            var gasPrice = 20.GWei();
            var info = new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, TestItem.KeccakB);
            _consumerTransactionsService.UpdateDepositGasPriceAsync(depositId, gasPrice).Returns(info);
            var result = await _rpc.ndm_updateDepositGasPrice(depositId, gasPrice);
            result.Data.Status.Should().Be(info.Status.ToString().ToLowerInvariant());
            result.Data.Hash.Should().Be(info.Hash);
            await _consumerTransactionsService.Received().UpdateDepositGasPriceAsync(depositId, gasPrice);
        }
        
        [Test]
        public async Task update_refund_gas_price_should_return_transaction_hash()
        {
            var depositId = TestItem.KeccakA;
            var gasPrice = 20.GWei();
            var info = new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, TestItem.KeccakB);
            _consumerTransactionsService.UpdateRefundGasPriceAsync(depositId, gasPrice).Returns(info);
            var result = await _rpc.ndm_updateRefundGasPrice(depositId, gasPrice);
            result.Data.Status.Should().Be(info.Status.ToString().ToLowerInvariant());
            result.Data.Hash.Should().Be(info.Hash);
            await _consumerTransactionsService.Received().UpdateRefundGasPriceAsync(depositId, gasPrice);
        }
        
        [Test]
        public async Task cancel_deposit_should_return_transaction_hash()
        {
            var depositId = TestItem.KeccakA;
            var info = new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, TestItem.KeccakB);
            _consumerTransactionsService.CancelDepositAsync(depositId).Returns(info);
            var result = await _rpc.ndm_cancelDeposit(depositId);
            result.Data.Status.Should().Be(info.Status.ToString().ToLowerInvariant());
            result.Data.Hash.Should().Be(info.Hash);
            await _consumerTransactionsService.Received().CancelDepositAsync(depositId);
        }
        
        [Test]
        public async Task cancel_refund_should_return_transaction_hash()
        {
            var depositId = TestItem.KeccakA;
            var info = new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, TestItem.KeccakB);
            _consumerTransactionsService.CancelRefundAsync(depositId).Returns(info);
            var result = await _rpc.ndm_cancelRefund(depositId);
            result.Data.Status.Should().Be(info.Status.ToString().ToLowerInvariant());
            result.Data.Hash.Should().Be(info.Hash);
            await _consumerTransactionsService.Received().CancelRefundAsync(depositId);
        }

        [Test]
        public async Task get_consumer_pending_transactions_should_return_data()
        {
            var pendingTransactions = new List<ResourceTransaction>
            {
                new ResourceTransaction(TestItem.KeccakA.ToString(), "test", TransactionInfo.Default(TestItem.KeccakB,
                    1.Ether(), 20.GWei(), 10, _timestamper.UnixTime.Seconds))
            };
            
            var transaction = pendingTransactions[0];
            _consumerTransactionsService.GetPendingAsync().Returns(pendingTransactions);
            var result = await _rpc.ndm_getConsumerPendingTransactions();
            await _consumerTransactionsService.Received().GetPendingAsync();
            result.Data.Should().NotBeEmpty();
            result.Data.Should().ContainSingle(t =>
                t.ResourceId == transaction.ResourceId && t.Type == transaction.Type &&
                t.Transaction.Hash == transaction.Transaction.Hash &&
                t.Transaction.Value == transaction.Transaction.Value &&
                t.Transaction.GasPrice == transaction.Transaction.GasPrice &&
                t.Transaction.GasLimit == transaction.Transaction.GasLimit &&
                t.Transaction.MaxFee == transaction.Transaction.GasPrice * transaction.Transaction.GasLimit &&
                t.Transaction.Timestamp == transaction.Transaction.Timestamp);
        }
        
        [Test]
        public void get_consumer_gas_limits_should_return_data()
        {
            var gasLimits = new GasLimits(55000, 55000);
            _gasLimitsService.GasLimits.Returns(gasLimits);
            var result = _rpc.ndm_getConsumerGasLimits();
            result.Data.Deposit.Should().Be(gasLimits.Deposit);
            result.Data.Refund.Should().Be(gasLimits.Refund);
        }

        private static void VerifyGasPrice(GasPriceDetailsForRpc rpcGasPrice, GasPriceDetails gasPrice)
        {
            rpcGasPrice.Price.Should().Be(gasPrice.Price);
            rpcGasPrice.WaitTime.Should().Be(gasPrice.WaitTime);
        }
        
        private static void VerifyDepositReportItem(DepositReportItemForRpc rpcItem, DepositReportItem item)
        {
            rpcItem.Id.Should().Be(item.Id);
            rpcItem.AssetId.Should().Be(item.AssetId);
            rpcItem.AssetName.Should().Be(item.AssetName);
            rpcItem.Provider.Should().Be(item.Provider);
            rpcItem.ProviderName.Should().Be(item.ProviderName);
            rpcItem.Value.Should().Be(item.Value);
            rpcItem.Units.Should().Be(item.Units);
            rpcItem.Completed.Should().Be(item.Completed);
            rpcItem.Timestamp.Should().Be(item.Timestamp);
            rpcItem.ExpiryTime.Should().Be(item.ExpiryTime);
            rpcItem.Expired.Should().Be(item.Expired);
            rpcItem.TransactionHash.Should().Be(item.TransactionHash);
            rpcItem.ConfirmationTimestamp.Should().Be(item.ConfirmationTimestamp);
            rpcItem.Confirmations.Should().Be(item.Confirmations);
            rpcItem.RequiredConfirmations.Should().Be(item.RequiredConfirmations);
            rpcItem.Confirmed.Should().Be(item.Confirmed);
            rpcItem.ClaimedRefundTransactionHash.Should().Be(item.ClaimedRefundTransactionHash);
            rpcItem.RefundClaimed.Should().Be(item.RefundClaimed);
            rpcItem.ConsumedUnits.Should().Be(item.ConsumedUnits);
            rpcItem.ClaimedUnits.Should().Be(item.ClaimedUnits);
            rpcItem.RefundedUnits.Should().Be(item.RefundedUnits);
            rpcItem.RemainingUnits.Should().Be(item.RemainingUnits);
            rpcItem.ClaimedValue.Should().Be(item.ClaimedValue);
            rpcItem.RefundedValue.Should().Be(item.RefundedValue);
            rpcItem.RemainingValue.Should().Be(item.RemainingValue);
            rpcItem.Receipts.Should().ContainSingle();
            item.Receipts.Should().ContainSingle();
            VerifyDataDeliveryReceiptReportItem(rpcItem.Receipts.Single(), item.Receipts.Single());
        }

        private static void VerifyDataDeliveryReceiptReportItem(DataDeliveryReceiptReportItemForRpc rpcReceipt,
            DataDeliveryReceiptReportItem receipt)
        {
            rpcReceipt.Id.Should().Be(receipt.Id);
            rpcReceipt.Number.Should().Be(receipt.Number);
            rpcReceipt.SessionId.Should().Be(receipt.SessionId);
            rpcReceipt.NodeId.Should().Be(receipt.NodeId);
            rpcReceipt.Timestamp.Should().Be(receipt.Timestamp);
            rpcReceipt.IsMerged.Should().Be(receipt.IsMerged);
            rpcReceipt.IsClaimed.Should().Be(receipt.IsClaimed);
            rpcReceipt.Request.Number.Should().Be(receipt.Request.Number);
            rpcReceipt.Request.DepositId.Should().Be(receipt.Request.DepositId);
            rpcReceipt.Request.UnitsRange.From.Should().Be(receipt.Request.UnitsRange.From);
            rpcReceipt.Request.UnitsRange.To.Should().Be(receipt.Request.UnitsRange.To);
            rpcReceipt.Request.IsSettlement.Should().Be(receipt.Request.IsSettlement);
            rpcReceipt.Receipt.StatusCode.Should().Be(receipt.Receipt.StatusCode.ToString().ToLowerInvariant());
            rpcReceipt.Receipt.ConsumedUnits.Should().Be(receipt.Receipt.ConsumedUnits);
            rpcReceipt.Receipt.UnpaidUnits.Should().Be(receipt.Receipt.UnpaidUnits);
        }

        private static void VerifyConsumerSession(ConsumerSessionForRpc session)
        {
            session.Id.Should().Be(Keccak.Zero);
            session.DepositId.Should().Be(TestItem.KeccakA);
            session.DataAssetId.Should().Be(TestItem.KeccakB);
            session.ConsumerAddress.Should().Be(TestItem.AddressA);
            session.ConsumerNodeId.Should().Be(TestItem.PublicKeyA);
            session.ProviderAddress.Should().Be(TestItem.AddressB);
            session.ProviderNodeId.Should().Be(TestItem.PublicKeyB);
            session.State.Should().Be(SessionState.Started.ToString().ToLowerInvariant());
            session.StartUnitsFromConsumer.Should().Be(0);
            session.StartUnitsFromProvider.Should().Be(0);
            session.StartTimestamp.Should().Be(0);
            session.FinishTimestamp.Should().Be(0);
            session.ConsumedUnits.Should().Be(0);
            session.UnpaidUnits.Should().Be(0);
            session.PaidUnits.Should().Be(0);
            session.SettledUnits.Should().Be(0);
            session.ConsumedUnitsFromProvider.Should().Be(0);
            session.DataAvailability.Should().Be(DataAvailability.Available.ToString().ToLowerInvariant());
        }

        private static void VerifyDepositDetails(DepositDetailsForRpc deposit)
        {
            deposit.Id.Should().Be(Keccak.OfAnEmptyString);
            deposit.Deposit.Should().NotBeNull();
            deposit.Deposit.Id.Should().Be(Keccak.OfAnEmptyString);
            deposit.Deposit.Units.Should().Be((uint?)1);
            deposit.Deposit.Value.Should().Be((UInt256?)UInt256.One);
            deposit.Deposit.ExpiryTime.Should().Be((uint?)DepositExpiryTime);
            deposit.Timestamp.Should().Be(1);
            deposit.Transaction.Hash.Should().Be(TestItem.KeccakA);
            deposit.Transaction.Value.Should().Be((UInt256?)UInt256.One);
            deposit.Transaction.GasPrice.Should().Be((UInt256?)UInt256.One);
            deposit.Transaction.GasLimit.Should().Be((ulong?)1);
            deposit.Transaction.MaxFee.Should().Be(deposit.Transaction.GasPrice * deposit.Transaction.GasLimit);
            deposit.Transaction.Timestamp.Should().Be(deposit.Transaction.Timestamp);
            deposit.Confirmed.Should().Be(false);
            deposit.Expired.Should().Be(false);
            deposit.RefundClaimed.Should().Be(false);
            deposit.ClaimedRefundTransaction.Should().BeNull();
            deposit.ConsumedUnits.Should().Be(0);
            deposit.Kyc.Should().BeNullOrEmpty();
            VerifyDataAsset(deposit.DataAsset);
        }

        private static void VerifyDataAsset(DataAssetForRpc dataAsset)
        {
            dataAsset.Should().NotBeNull();
            dataAsset.Id.Should().NotBeNull();
            dataAsset.Name.Should().Be("test");
            dataAsset.Description.Should().Be("test");
            dataAsset.UnitPrice.Should().Be((UInt256?)UInt256.One);
            dataAsset.UnitType.Should().Be(DataAssetUnitType.Unit.ToString().ToLowerInvariant());
            dataAsset.QueryType.Should().Be(QueryType.Stream.ToString().ToLowerInvariant());
            dataAsset.MinUnits.Should().Be(0);
            dataAsset.MaxUnits.Should().Be(10);
            dataAsset.Rules.Should().NotBeNull();
            dataAsset.Rules.Expiry.Should().NotBeNull();
            dataAsset.Rules.Expiry.Value.Should().Be(1);
            dataAsset.Rules.UpfrontPayment.Should().BeNull();
            dataAsset.Provider.Address.Should().Be(Address.Zero);
            dataAsset.Provider.Name.Should().Be("test");
            dataAsset.File.Should().BeNullOrEmpty();
            dataAsset.Data.Should().BeNullOrEmpty();
            dataAsset.State.Should().Be(DataAssetState.Unpublished.ToString().ToLowerInvariant());
            dataAsset.TermsAndConditions.Should().BeNullOrEmpty();
            dataAsset.KycRequired.Should().BeFalse();
        }

        private static ConsumerSession GetConsumerSession()
            => new ConsumerSession(Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB, TestItem.AddressA,
                TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.Started,
                0, 0, dataAvailability: DataAvailability.Available);

        private static DataAsset GetDataAsset()
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                DataAssetUnitType.Unit, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));

        private static DepositDetails GetDepositDetails()
            => new DepositDetails(new Deposit(Keccak.OfAnEmptyString, 1, DepositExpiryTime, 1),
                GetDataAsset(), TestItem.AddressB, Array.Empty<byte>(), 1,
                new[] {TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1)});

        private static DepositReportItem GetDepositReportItem()
            => new DepositReportItem(Keccak.Zero, TestItem.KeccakA, "test", TestItem.AddressA,
                "test", 1, 2, TestItem.AddressB, 1, DepositExpiryTime, false, TestItem.KeccakA,
                1, 1, 1, true, false, TestItem.KeccakB, false, 1, new[]
                {
                    new DataDeliveryReceiptReportItem(Keccak.Zero, 1, TestItem.KeccakC, TestItem.PublicKeyA,
                        new DataDeliveryReceiptRequest(1, TestItem.KeccakD, new UnitsRange(0, 1), false,
                            new[] {new DataDeliveryReceiptToMerge(new UnitsRange(0, 1), new Signature(0, 0, 27))}),
                        new DataDeliveryReceipt(StatusCodes.Ok, 1, 1, new Signature(0, 0, 27)), 1, false, false)
                });
    }
}
