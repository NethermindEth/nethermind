using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Database;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Providers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Policies;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.DataMarketplace.Providers.Test.Consumers;
using Nethermind.DataMarketplace.Providers.Validators;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Services
{
    public class ProviderServiceTests
    {
        IProviderService providerService;
        IConfigManager configManager;
        string configId; 
        IConsumerRepository consumerRepository; 
        IProviderDepositApprovalRepository providerDepositApprovalRepository;
        IPaymentClaimRepository paymentClaimRepository;
        IPaymentClaimProcessor paymentClaimProcessor; 
        IProviderSessionRepository sessionRepository;
        IDataAssetRepository dataAssetRepository;
        ITimestamper timestamper;
        IEcdsa ecdsa;
        IAbiEncoder abiEncoder;
        INdmDataPublisher ndmDataPublisher;
        IGasPriceService gasPriceService;
        IDataAvailabilityValidator dataAvaliabilityValidator;
        ISessionManager sessionManager; 
        ITransactionVerifier transactionVerifier;
        IDepositManager depositManager;
        IRefundPolicy refundPolicy;
        IDepositService depositService;
        IWallet wallet;
        INdmBlockchainBridge blockchainBridge;
        Address providerAddress;
        Address coldWalletAddress;
        PublicKey nodeId;
        string providerName;
        string filesPath;
        double fileMaxSize;
        uint requiredBlockConfirmations;
        ulong paymentGasLimit;
        ILogManager logManager;
        bool skipDepositVerification;
        bool backgroundServicesDisabled;
        Timer timer;
        bool accountLocked;
        NdmConfig config;
        TestConsumer testConsumer;
        MemDb paymentClaimDb;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            InitDecoders();
            SetUpSubstitutes();

            logManager = NoErrorLimboLogs.Instance;
            timestamper = new Timestamper();

            config = new NdmConfig();
            configId = config.Id;

            var configDb = new MemDb(NdmDbNames.Configs);
            var configRepository = new ConfigRocksRepository(configDb, new NdmConfigDecoder());
            await configRepository.AddAsync(config);
            configManager = new ConfigManager(config, configRepository);

            config.ProviderAddress = "c7f8522f15c189e00d2f895b4528b4f84516cd7b";
            config.ProviderColdWalletAddress = "c7f8522f15c189e00d2f895b4528b4f84516cd7b";

            config.ConsumerAddress = "a238812fb5c199ea051f89200028b4f08513cd6a";

            providerAddress = new Address(config.ProviderAddress);
            coldWalletAddress = new Address(config.ProviderColdWalletAddress);

            var sessionDb = new MemDb(ProviderDbNames.ProviderSessions);
            sessionRepository = new ProviderSessionRocksRepository(sessionDb, new ProviderSessionDecoder());
            sessionManager = new SessionManager(sessionRepository, timestamper, logManager);

            var consumersDb = new MemDb(ProviderDbNames.Consumers);
            consumerRepository = new ConsumerRocksRepository(consumersDb, new ConsumerDecoder());

            paymentClaimDb = new MemDb(ProviderDbNames.PaymentClaims);
            paymentClaimRepository = new PaymentClaimRocksRepository(paymentClaimDb, new PaymentClaimDecoder());

            var dataAssetDb = new MemDb(ProviderDbNames.DataAssets);
            dataAssetRepository = new DataAssetRocksRepository(dataAssetDb, new DataAssetDecoder());

            ecdsa = new Ecdsa();
            abiEncoder = new AbiEncoder();
            ndmDataPublisher = new NdmDataPublisher();
            dataAvaliabilityValidator = new DataAvailabilityValidator();

            nodeId = new PublicKey(new CryptoRandom().GenerateRandomBytes(64));
            providerName = "test";
            filesPath = config.FilesPath;
            fileMaxSize = config.FileMaxSize;
            requiredBlockConfirmations = config.BlockConfirmations; 
            paymentGasLimit = 70000;
            skipDepositVerification = true;
            backgroundServicesDisabled = false;

            DataAsset dataAsset = new DataAsset(new Keccak(Keccak.OfAnEmptyString.ToString()), "TestDataAsset", "Testing", 1, DataAssetUnitType.Unit, 1, 10, new DataAssetRules(new DataAssetRule(10)), new DataAssetProvider(providerAddress, "provider"), null, QueryType.Query);

            await dataAssetRepository.AddAsync(dataAsset);

            paymentClaimProcessor.SendTransactionAsync(Arg.Any<PaymentClaim>(), Arg.Any<UInt256>()).Returns(new Keccak("0x77a0e79f851c097f81210d88eb59ed8f933336f65e88b0bde6506f2c6556c2b6"));

            gasPriceService.GetCurrentPaymentClaimGasPriceAsync().Returns(Task.FromResult(new UInt256(10)));

            providerService = new ProviderService(configManager, configId, consumerRepository, dataAssetRepository, providerDepositApprovalRepository, paymentClaimRepository, paymentClaimProcessor, sessionRepository, timestamper, ecdsa, abiEncoder, ndmDataPublisher, gasPriceService, dataAvaliabilityValidator, sessionManager, transactionVerifier, depositManager, refundPolicy, depositService, wallet, blockchainBridge, providerAddress, coldWalletAddress, nodeId, providerName, filesPath, fileMaxSize, requiredBlockConfirmations, paymentGasLimit, logManager, skipDepositVerification, backgroundServicesDisabled);
        }  

        [Test, Order(1)]
        public void returns_correct_address()
        {
            var address = providerService.GetAddress();
            Assert.AreEqual(address, providerAddress);
        }

        [Test, Order(2)]
        public void returns_correct_cold_wallet_address()
        {
            var address = providerService.GetColdWalletAddress();
            Assert.AreEqual(address, coldWalletAddress);
        }

        [Test, Order(3)]
        public async Task can_change_address()
        {
            var newAddress = new Address("0xa7f8522f15a189e03d2f895b452814f84516cd7b");
            await providerService.ChangeAddressAsync(newAddress);

            depositManager.Received().ChangeAddress(newAddress);
            Assert.AreEqual(providerService.GetAddress(), newAddress);
        }

        [Test, Order(4)]
        public async Task can_change_cold_wallet_address()
        {
            var newAddress = new Address("0xa7f8522f15a189e03d2f895b452814f84516cd7b");
            await providerService.ChangeColdWalletAddressAsync(newAddress);

            depositManager.Received().ChangeColdWalletAddress(newAddress);
            Assert.AreEqual(providerService.GetColdWalletAddress(), newAddress);
        }

        [Test, Order(5)]
        public void can_add_consumer()
        {
            testConsumer = TestConsumer.ForDeposit(Keccak.Zero)
                .WithNode(1).AddSession().WithUnpaidUnits(10)
                .And.Build();

            ConsumerNode consumerNode = testConsumer.Node(1).Node;
            INdmProviderPeer consumerPeer = consumerNode.Peer;

            ConsumerNode[] pre = sessionManager.GetConsumerNodes().ToArray();
            bool consumerAlreadyAdded = pre.Contains(consumerNode);
            Assert.IsTrue(!consumerAlreadyAdded);

            providerService.AddConsumerPeer(consumerPeer);
            var addedConsumer = sessionManager.GetConsumerNodes().Where(node => node.Peer.Equals(consumerPeer));

            Assert.IsTrue(addedConsumer != null);
        }

        [Test]
        public async Task can_send_transaction_when_claim_status_unknown()
        {
            PaymentClaim claim = GenerateTestClaim(PaymentClaimStatus.Unknown, "can_send_transaction_when_claim_status_unknown");

            var testTransaction = new NdmTransaction(new Transaction(), false, 5, Keccak.OfAnEmptyString, 0);

            await paymentClaimRepository.AddAsync(claim);
            blockchainBridge.GetTransactionAsync(claim.Transaction.Hash).Returns(Task.FromResult(testTransaction));

            transactionVerifier.VerifyAsync(testTransaction).Returns(Task.FromResult(new TransactionVerifierResult(true, 5, 2)));

            WaitForPaymentClaimsProcessing();

            var addedClaim = await paymentClaimRepository.GetAsync(claim.Id);
            Assert.IsTrue(addedClaim.Status == PaymentClaimStatus.Sent); 
        }

        [Test]
        public async Task can_claim_payment()
        {
            PaymentClaim claim = GenerateTestClaim(PaymentClaimStatus.Sent, "can_claim_payment");

            await paymentClaimRepository.AddAsync(claim);

            var testTransaction = new NdmTransaction(new Transaction(), false, 5, Keccak.OfAnEmptyString, 1);

            blockchainBridge.GetTransactionAsync(claim.Transaction.Hash).Returns(Task.FromResult(testTransaction));

            transactionVerifier.VerifyAsync(testTransaction).Returns(Task.FromResult(new TransactionVerifierResult(true, 10, 2)));

            WaitForPaymentClaimsProcessing();

            var addedClaim = await paymentClaimRepository.GetAsync(claim.Id);
            Assert.IsTrue(addedClaim.Status == PaymentClaimStatus.Claimed);
        }

        [Test]
        public async Task will_claim_with_enough_confirmations()
        {
            PaymentClaim claim = GenerateTestClaim(PaymentClaimStatus.Sent, "will_claim_with_enough_confirmations");

            await paymentClaimRepository.AddAsync(claim);

            var testTransaction = new NdmTransaction(new Transaction(), false, 1, Keccak.OfAnEmptyString, 5);

            blockchainBridge.GetTransactionAsync(claim.Transaction.Hash).Returns(Task.FromResult(testTransaction));

            transactionVerifier.VerifyAsync(testTransaction).Returns(Task.FromResult(new TransactionVerifierResult(true, 5, 5)));

            WaitForPaymentClaimsProcessing();
        
            var addedClaim = await paymentClaimRepository.GetAsync(claim.Id);
            Assert.IsTrue(addedClaim.Status == PaymentClaimStatus.Claimed);
        }

        [Test]
        public async Task will_not_claim_without_enough_confirmations()
        {
            PaymentClaim claim = GenerateTestClaim(PaymentClaimStatus.Sent, "will_not_claim_without_enough_confirmations");

            await paymentClaimRepository.AddAsync(claim);

            var testTransaction = new NdmTransaction(new Transaction(), false, 1, Keccak.OfAnEmptyString, 5);

            blockchainBridge.GetTransactionAsync(claim.Transaction.Hash).Returns(Task.FromResult(testTransaction));

            transactionVerifier.VerifyAsync(testTransaction).Returns(Task.FromResult(new TransactionVerifierResult(true, 2, 5)));

            WaitForPaymentClaimsProcessing();
        
           var addedClaim = await paymentClaimRepository.GetAsync(claim.Id);
            Assert.IsTrue(addedClaim.Status != PaymentClaimStatus.Claimed);
        }

        [Test]
        public async Task can_add_data_asset()
        {
            Keccak id = await providerService.AddDataAssetAsync("test-asset", "Testing", new UInt256(10), DataAssetUnitType.Unit, 1, 10, new DataAssetRules(new DataAssetRule(10)));

            var addedDataAsset = dataAssetRepository.GetAsync(id); 

            Assert.IsNotNull(addedDataAsset);
        }

        [Test]
        public async Task can_remove_data_asset()
        {
            Keccak id = await providerService.AddDataAssetAsync("test-asset2", "Testing2", new UInt256(1), DataAssetUnitType.Unit, 1, 10, new DataAssetRules(new DataAssetRule(10)));
            
            bool isRemoved = await providerService.RemoveDataAssetAsync(id);

            if(isRemoved)
            {
                var removedDataAsset = await dataAssetRepository.GetAsync(id);
                Assert.IsNull(removedDataAsset);
            }
            else
            {
                Assert.Fail("Added data asset was not removed");
            }
        }

        [Test]
        public async Task will_not_remove_archived_asset()
        {
            Keccak id = await providerService.AddDataAssetAsync("test-asset2", "Testing2", new UInt256(1), DataAssetUnitType.Unit, 1, 10, new DataAssetRules(new DataAssetRule(10)));

            var asset = await dataAssetRepository.GetAsync(id);
            asset.SetState(DataAssetState.Archived);
            await dataAssetRepository.UpdateAsync(asset);

            var isRemoved = await providerService.RemoveDataAssetAsync(id);

            Assert.IsFalse(isRemoved);
        }

        [Test]
        public async Task will_send_early_refund_ticket()
        {
            testConsumer = TestConsumer.ForDeposit(Keccak.Zero)
                .WithNode(1).AddSession().WithUnpaidUnits(10)
                .And.Build();

            await consumerRepository.AddAsync(testConsumer.Consumer);            
            var consumerNode = testConsumer.Node(1).Node;

            sessionManager.AddPeer(consumerNode.Peer);
            sessionManager.SetSession(consumerNode.Sessions.First(), consumerNode.Peer);

            refundPolicy.GetClaimableAfterUnits(testConsumer.DepositId).Returns((uint)1);

            var depositId = await providerService.SendEarlyRefundTicketAsync(testConsumer.DepositId);

            consumerNode.Peer.Received().SendEarlyRefundTicket(Arg.Any<EarlyRefundTicket>(), RefundReason.DataDiscontinued);
            Assert.IsNotNull(depositId);
        }

        [Test]
        public void will_add_plugin_to_the_pool()
        {
            var plugin = GenerateTestPlugin("TestPlugin");

            Assert.DoesNotThrowAsync(() => providerService.InitPluginAsync(plugin));

            var plugins = providerService.GetPlugins();
            Assert.IsTrue(plugins.Contains(plugin.Name.ToLowerInvariant()));
        }

        [Test]
        public void will_not_add_the_same_plugin_to_the_pool()
        {
            var plugin = GenerateTestPlugin("SomePlugin");
            var duplicatedPlugin = GenerateTestPlugin("SomePlugin");

            Assert.DoesNotThrowAsync(() => providerService.InitPluginAsync(plugin));
            Assert.ThrowsAsync<Exception>(() => providerService.InitPluginAsync(duplicatedPlugin));
        }


        //we need to wait in order to let provider run it's payment checking loop and process newly added claim
        private void WaitForPaymentClaimsProcessing()
        {
            Thread.Sleep(6000);
        }

        private void InitDecoders()
        {
            //initialize static constructors of needed decoders
            DataAssetDecoder.Init();
            UnitsRangeDecoder.Init();
            TransactionInfoDecoder.Init();
            DataAssetRulesDecoder.Init();
            DataAssetRuleDecoder.Init();
            DataRequestDecoder.Init();
            DataAssetProviderDecoder.Init();
        }

        private void SetUpSubstitutes()
        {
            transactionVerifier = Substitute.For<ITransactionVerifier>();
            paymentClaimProcessor = Substitute.For<IPaymentClaimProcessor>();
            depositManager = Substitute.For<IDepositManager>();
            providerDepositApprovalRepository = Substitute.For<IProviderDepositApprovalRepository>();
            gasPriceService = Substitute.For<IGasPriceService>();
            wallet = NullWallet.Instance;
            blockchainBridge = Substitute.For<INdmBlockchainBridge>();
            refundPolicy = Substitute.For<IRefundPolicy>();
        }

        private PaymentClaim GenerateTestClaim(PaymentClaimStatus claimStatus, string claimName)
        {
            byte[] bytes = new byte[32];

            new Random().NextBytes(bytes);
            Keccak transactionKeccak = Keccak.Compute(bytes);

            new Random().NextBytes(bytes);
            Keccak claimKeccak = Keccak.Compute(bytes);

            TransactionInfo transaction = TransactionInfo.Default(transactionKeccak, 10, 0, 10000, 10);
            transaction.SetIncluded();

            return new PaymentClaim(claimKeccak, Keccak.Zero, Keccak.Zero, claimName, 10, 0, new UnitsRange(1, 10), 10, 1, 0, new byte[32], providerAddress, new Address(config.ConsumerAddress), new Signature(1, 2, 37), 10000, new TransactionInfo[] { transaction }, claimStatus);
        }

        private INdmPlugin GenerateTestPlugin(string pluginName)
        {
            var plugin = Substitute.For<INdmPlugin>();
            plugin.Name.Returns(pluginName);

            return plugin;
        }
    }
}