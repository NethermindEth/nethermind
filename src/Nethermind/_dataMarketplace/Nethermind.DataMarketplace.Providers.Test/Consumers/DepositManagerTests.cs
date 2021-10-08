using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Policies;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.Int256;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Consumers
{
    internal class DepositManagerTests
    {
        private IDepositNodesHandlerFactory _depositNodesHandlerFactory;
        private IDepositNodesHandler _depositNodesHandler;
        private ISessionManager _sessionManager;
        private IReceiptsPolicies _receiptsPolicies;
        private IReceiptProcessor _receiptProcessor;
        private IPaymentClaimProcessor _paymentClaimProcessor;
        private IConsumerRepository _consumerRepository;
        private IPaymentClaimRepository _paymentClaimRepository;
        private IProviderSessionRepository _sessionRepository;
        private IReceiptRepository _receiptRepository;
        private ITimestamper _timestamper;
        private IGasPriceService _gasPriceService;
        private ILogManager _logManager;
        private IDepositManager _depositManager;
        private IWallet _wallet;
        private Address _address;
        private Address _consumer;

        [SetUp]
        public void Setup()
        {
            _depositNodesHandlerFactory = Substitute.For<IDepositNodesHandlerFactory>();
            _sessionManager = Substitute.For<ISessionManager>();
            _receiptsPolicies = Substitute.For<IReceiptsPolicies>();
            _receiptProcessor = Substitute.For<IReceiptProcessor>();
            _paymentClaimProcessor = Substitute.For<IPaymentClaimProcessor>();
            _consumerRepository = Substitute.For<IConsumerRepository>();
            _paymentClaimRepository = Substitute.For<IPaymentClaimRepository>();
            _sessionRepository = Substitute.For<IProviderSessionRepository>();
            _sessionRepository.BrowseAsync(Arg.Any<GetProviderSessions>()).Returns(PagedResult<ProviderSession>.Empty);
            _receiptRepository = Substitute.For<IReceiptRepository>();
            var unixTime = UnixTime.FromSeconds(100);
            _timestamper = Substitute.For<ITimestamper>();
            _timestamper.UnixTime.Returns(unixTime);
            _gasPriceService = Substitute.For<IGasPriceService>();
            _logManager = Substitute.For<ILogManager>();
            _wallet = Substitute.For<IWallet>();
            _address = Address.Zero;
            _consumer = Address.Zero;
            _depositNodesHandler = new InMemoryDepositNodesHandler(Keccak.Zero, _consumer, DataAssetUnitType.Unit, 0,
                100, 0,
                0, 0, 0, 0, 0, 0, null, null, 0);
            _depositNodesHandlerFactory.CreateInMemory(Arg.Any<Keccak>(), _consumer, Arg.Any<DataAssetUnitType>(),
                Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<UInt256>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<uint>(),
                Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<PaymentClaim>(),
                Arg.Any<IEnumerable<DataDeliveryReceiptDetails>>(), Arg.Any<uint>()).Returns(_depositNodesHandler);
            _depositManager = new DepositManager(_depositNodesHandlerFactory, _sessionManager, _receiptsPolicies,
                _wallet, _address, _receiptProcessor, _paymentClaimProcessor, _consumerRepository,
                _paymentClaimRepository, _receiptRepository, _sessionRepository, _timestamper, _gasPriceService,
                _logManager);
        }

        [Test]
        // While running HandleUnpaidUnitsAsync() DepositManager will try to claim payment for
        // unpaid units.
        // The method starts with previous session and checks whether there are any unpaid units and then
        // moves to the current one. 
        public async Task can_handle_unpaid_units()
        {
            var depositId = Keccak.Zero;
            TestConsumer consumer = TestConsumer.ForDeposit(depositId)
                            .WithNode(1).AddSession().WithUnpaidUnits(10)
                            .And.WithNode(2).AddSession().WithUnpaidUnits(20)
                            .And.Build();

            ConfigureMocks(consumer);

            _sessionManager.GetSession(depositId, consumer.Node(1).Node.Peer)
                            .Returns(consumer.Node(1).Node.Sessions.First(s => s.DepositId == depositId));

            IDepositNodesHandler depositHandler = await _depositManager.InitAsync(depositId);
            await _depositManager.HandleUnpaidUnitsAsync(depositId, consumer.Node(1).Node.Peer);

            Assert.IsTrue(depositHandler.UnpaidUnits == 0);
        }

        [Test]
        // Merging recipts runs when consumer consumed all of the purchased units or there is enough unmerged units to be above merge threshold (unmergedUnits * unitPrice >= mergeThreshold).
        public async Task can_merge_recipts_when_consumed_all_units()
        {
            var depositId = Keccak.Zero;
            TestConsumer consumer = TestConsumer.ForDeposit(depositId)
                            .WithNode(1).AddSession().WithUnpaidUnits(10)
                            .And.WithNode(2).AddSession().WithUnpaidUnits(20)
                            .And.Build();

            ConfigureMocks(consumer);

            _sessionManager.GetSession(depositId, consumer.Node(1).Node.Peer)
                            .Returns(consumer.Node(1).Node.Sessions.First(s => s.DepositId == depositId));

            IDepositNodesHandler depositHandler = await _depositManager.InitAsync(depositId);

            AddReciptsToMerge(consumer, depositHandler);

            depositHandler.SetConsumedUnits(100);
            depositHandler.SetUnmergedUnits(50);

            await _depositManager.HandleUnpaidUnitsAsync(depositId, consumer.Node(1).Node.Peer);

            Assert.IsTrue(depositHandler.UnmergedUnits == 0);
        }

        [Test]
        // Total purchased units = 100 so there is 20 left which are not consumed,
        // in that case recipts should not be merged.
        public async Task will_not_merge_if_has_not_consumed_all_units_and_did_not_match_threshold()
        {
            var depositId = Keccak.Zero;
            TestConsumer consumer = TestConsumer.ForDeposit(depositId)
                            .WithNode(1).AddSession().WithUnpaidUnits(10)
                            .And.WithNode(2).AddSession().WithUnpaidUnits(20)
                            .And.Build();

            ConfigureMocks(consumer);

            _sessionManager.GetSession(depositId, consumer.Node(1).Node.Peer)
                            .Returns(consumer.Node(1).Node.Sessions.First(s => s.DepositId == depositId));

            IDepositNodesHandler depositHandler = await _depositManager.InitAsync(depositId);

            AddReciptsToMerge(consumer, depositHandler);
            _receiptsPolicies.CanMergeReceipts(depositHandler.UnmergedUnits, depositHandler.UnitPrice).Returns(false);

            depositHandler.SetConsumedUnits(80);
            depositHandler.SetUnmergedUnits(50);

            await _depositManager.HandleUnpaidUnitsAsync(depositId, consumer.Node(1).Node.Peer);

            Assert.IsTrue(depositHandler.UnmergedUnits == 50);
        }

        [Test]
        // There is no need to match merge threshold while all of the units has been consumed
        public async Task will_merge_if_consumed_all_but_did_not_match_threshold()
        {
            var depositId = Keccak.Zero;
            TestConsumer consumer = TestConsumer.ForDeposit(depositId)
                            .WithNode(1).AddSession().WithUnpaidUnits(10)
                            .And.WithNode(2).AddSession().WithUnpaidUnits(20)
                            .And.Build();

            ConfigureMocks(consumer);

            _sessionManager.GetSession(depositId, consumer.Node(1).Node.Peer)
                            .Returns(consumer.Node(1).Node.Sessions.First(s => s.DepositId == depositId));

            IDepositNodesHandler depositHandler = await _depositManager.InitAsync(depositId);

            AddReciptsToMerge(consumer, depositHandler);
            _receiptsPolicies.CanMergeReceipts(depositHandler.UnmergedUnits, depositHandler.UnitPrice).Returns(false);

            depositHandler.SetConsumedUnits(100);
            depositHandler.SetUnmergedUnits(50);

            await _depositManager.HandleUnpaidUnitsAsync(depositId, consumer.Node(1).Node.Peer);

            Assert.IsTrue(depositHandler.UnmergedUnits == 0);
        }

        [Test]
        public async Task returns_unclaimed_units_correctly()
        {
            var depositId = Keccak.Zero;
            TestConsumer consumer = TestConsumer.ForDeposit(depositId)
                            .WithNode(1).AddSession().WithUnpaidUnits(10)
                            .And.WithNode(2).AddSession().WithUnpaidUnits(20)
                            .And.Build();

            ConfigureMocks(consumer);

            _sessionManager.GetSession(depositId, consumer.Node(1).Node.Peer)
                            .Returns(consumer.Node(1).Node.Sessions.First(s => s.DepositId == depositId));

            IDepositNodesHandler depositHandler = await _depositManager.InitAsync(depositId);

            AddReciptsToMerge(consumer, depositHandler);
            _receiptsPolicies.CanMergeReceipts(depositHandler.UnmergedUnits, depositHandler.UnitPrice).Returns(true);

            depositHandler.SetConsumedUnits(80);
            depositHandler.SetUnmergedUnits(50);

            await _depositManager.HandleUnpaidUnitsAsync(depositId, consumer.Node(1).Node.Peer);

            var unclaimedUnits = _depositManager.GetUnclaimedUnits(depositId);

            Assert.AreEqual(10, unclaimedUnits);
        }

        [Test]
        public async Task will_calculate_units_correctly_when_unit_type_is_time()
        {
            var depositId = Keccak.Zero;
            TestConsumer consumer = TestConsumer.ForDeposit(depositId, DataAssetUnitType.Time)
                .WithNode(1).AddSession().WithUnpaidUnits(10).WithConsumedUnits(30)
                .And.Build();

            ConfigureMocks(consumer);

            var depositNodesHandler = new InMemoryDepositNodesHandler(Keccak.Zero, 
                                                                    _consumer, 
                                                                    DataAssetUnitType.Time, 
                                                                    1,
                                                                    100, 
                                                                    1,
                                                                    60, 
                                                                    50, 
                                                                    30, 
                                                                    50, 
                                                                    0, 
                                                                    0, 
                                                                    null, 
                                                                    null, 
                                                                    0);

            _depositNodesHandlerFactory.CreateInMemory(depositId, 
                                                    Arg.Any<Address>(), 
                                                    DataAssetUnitType.Time, Arg.Any<uint>(), 
                                                    Arg.Any<uint>(), Arg.Any<UInt256>(), Arg.Any<uint>(), 
                                                    Arg.Any<uint>(),
                                                    Arg.Any<uint>(), 
                                                    Arg.Any<uint>(), 
                                                    Arg.Any<uint>(), 
                                                    Arg.Any<uint>(), 
                                                    Arg.Any<PaymentClaim>(), 
                                                    Arg.Any<IEnumerable<DataDeliveryReceiptDetails>>(), 
                                                    Arg.Any<uint>())
                                                    .Returns(depositNodesHandler);

            IDepositNodesHandler depositHandler = await _depositManager.InitAsync(depositId);

            await _depositManager.HandleConsumedUnitAsync(depositId);

            Assert.IsTrue(depositHandler.ConsumedUnits == 99);
            Assert.IsTrue(depositHandler.UnpaidUnits == 79);
            Assert.IsTrue(depositHandler.UnclaimedUnits == 89);
            Assert.IsTrue(depositHandler.UnmergedUnits == 69);
        }


        [Test]
        public async Task given_multiple_nodes_for_the_same_deposit_receipts_requests_should_have_valid_ranges()
        {
            var depositId = Keccak.Zero;
            var consumer = TestConsumer.ForDeposit(depositId)
                .WithNode(1).AddSession().WithUnpaidUnits(10)
                .And.WithNode(2).AddSession().WithUnpaidUnits(10)
                .And.WithNode(3).AddSession().WithUnpaidUnits(20)
                .And.WithNode(4).AddSession().WithUnpaidUnits(10)
                .And.WithNode(5).AddSession().WithUnpaidUnits(20)
                .And.Build();

            ConfigureMocks(consumer);

            await _depositManager.InitAsync(depositId);
            await _depositManager.HandleConsumedUnitAsync(depositId);

            consumer.AddReceipts(_depositNodesHandler.Receipts.ToArray());
            consumer.Node(1).ShouldDeliverReceiptWithinRange(1, 0, 9);
            consumer.Node(2).ShouldDeliverReceiptWithinRange(2, 10, 19);
            consumer.Node(3).ShouldDeliverReceiptWithinRange(3, 20, 39);
            consumer.Node(4).ShouldDeliverReceiptWithinRange(4, 40, 49);
            consumer.Node(5).ShouldDeliverReceiptWithinRange(5, 50, 69);
        }

        [Test]
        public async Task given_some_failing_nodes_for_the_same_deposit_receipts_requests_should_have_valid_ranges()
        {
            var depositId = Keccak.Zero;
            var consumer = TestConsumer.ForDeposit(depositId)
                .WithNode(1).AddSession().WithUnpaidUnits(10)
                .And.WithNode(2).AddSession().WithUnpaidUnits(10).Node.WillNotDeliverReceipt()
                .And.WithNode(3).AddSession().WithUnpaidUnits(20)
                .And.WithNode(4).AddSession().WithUnpaidUnits(20).Node.WillNotDeliverReceipt()
                .And.WithNode(5).AddSession().WithUnpaidUnits(20)
                .And.Build();
            var receipts = _depositNodesHandler.Receipts.ToList();


            ConfigureMocks(consumer);

            await _depositManager.InitAsync(depositId);
            await _depositManager.HandleConsumedUnitAsync(depositId);

            consumer.AddReceipts(_depositNodesHandler.Receipts.ToArray());
            consumer.Node(1).ShouldDeliverReceiptWithinRange(1, 0, 9);
            consumer.Node(2).ShouldNotDeliverReceipt();
            consumer.Node(3).ShouldDeliverReceiptWithinRange(3, 10, 29);
            consumer.Node(4).ShouldNotDeliverReceipt();
            consumer.Node(5).ShouldDeliverReceiptWithinRange(5, 30, 49);
        }

        [Test]
        public async Task given_next_iteration_for_receipts_requests_calculated_ranges_should_be_still_valid()
        {
            var depositId = Keccak.Zero;
            var consumer = TestConsumer.ForDeposit(depositId)
                .WithNode(1).AddSession().WithUnpaidUnits(10)
                .And.WithNode(2).AddSession().WithUnpaidUnits(10)
                .And.WithNode(3).AddSession().WithUnpaidUnits(20)
                .And.Build();

            ConfigureMocks(consumer);

            await _depositManager.InitAsync(depositId);
            await _depositManager.HandleConsumedUnitAsync(depositId);

            consumer.AddReceipts(_depositNodesHandler.Receipts.ToArray());
            consumer.Node(1).ShouldDeliverReceiptWithinRange(1, 0, 9);
            consumer.Node(2).ShouldDeliverReceiptWithinRange(2, 10, 19);
            consumer.Node(3).ShouldDeliverReceiptWithinRange(3, 20, 39);
        }

        private void ConfigureMocks(TestConsumer consumer)
        {
            var depositId = consumer.DepositId;
            _consumerRepository.GetAsync(depositId).Returns(consumer.Consumer);

            _paymentClaimRepository.BrowseAsync(Arg.Any<GetPaymentClaims>())
                .Returns(PagedResult<PaymentClaim>.Empty);

            _sessionRepository.BrowseAsync(new GetProviderSessions
                {
                    DepositId = depositId
                })
                .Returns(PagedResult<ProviderSession>.Create(new List<ProviderSession>(consumer.Sessions), 1, 1, 1, 1));

            _receiptsPolicies.CanRequestReceipts(Arg.Any<long>(), Arg.Any<UInt256>()).Returns(true);

            _sessionManager.GetConsumerNodes(depositId).Returns(consumer.Nodes.Select(n => n.Node).ToArray());

            _receiptProcessor.TryProcessAsync(Arg.Any<ProviderSession>(), _consumer, Arg.Any<INdmProviderPeer>(),
                Arg.Any<DataDeliveryReceiptRequest>(), Arg.Any<DataDeliveryReceipt>()).Returns(true);
        }

        private void AddReciptsToMerge(TestConsumer consumer, IDepositNodesHandler depositHandler)
        {
            DataDeliveryReceiptRequest request = new DataDeliveryReceiptRequest(1, consumer.DepositId, new UnitsRange(0, 5), false, new List<DataDeliveryReceiptToMerge> { new DataDeliveryReceiptToMerge(new UnitsRange(0,1), new Signature(1, 2, 37)) });
            DataDeliveryReceipt receipt = new DataDeliveryReceipt(StatusCodes.Ok, 50, 0, new Signature(1, 2, 37));
            DataDeliveryReceiptDetails receiptDetails = new DataDeliveryReceiptDetails(Keccak.OfAnEmptyString, consumer.Sessions.First().Id, consumer.DataAsset.Id, null, request, receipt, 10, true); 

            depositHandler.AddReceipt(receiptDetails);

            DataDeliveryReceiptRequest request2 = new DataDeliveryReceiptRequest(1, consumer.DepositId, new UnitsRange(6, 49));
            DataDeliveryReceipt receipt2 = new DataDeliveryReceipt(StatusCodes.Ok, 50, 0, new Signature(1, 2, 37));
            DataDeliveryReceiptDetails receiptDetails2 = new DataDeliveryReceiptDetails(Keccak.OfAnEmptyString, consumer.Sessions.First().Id, consumer.DataAsset.Id, null, request2, receipt2, 10, false); 

            depositHandler.AddReceipt(receiptDetails2);
        }
    }
}