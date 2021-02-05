using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Int256;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.KeyStore.ConsoleHelpers;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Tools.Refunder
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Rlp.RegisterDecoders(typeof(DepositDecoder).Assembly);
            Rlp.RegisterDecoders(typeof(DepositDetailsDecoder).Assembly);

            string dbPath = args[0];
            ConsoleAsyncLogger asyncLogger = new ConsoleAsyncLogger(LogLevel.Info);
            OneLoggerLogManager logManager = new OneLoggerLogManager(asyncLogger);

            var deposits = await LoadDeposits(logManager, dbPath);

            IKeyStore keyStore = BuildKeyStore(logManager);
            DevKeyStoreWallet wallet = new DevKeyStoreWallet(keyStore, logManager, false);

            foreach (var depositGroup in deposits.Items.GroupBy(d => d.Consumer))
            {
                Console.WriteLine($"Deposits by {depositGroup.Key}");
                foreach (DepositDetails depositDetails in depositGroup)
                {
                    DateTimeOffset dto = DateTimeOffset.FromUnixTimeSeconds(depositDetails.Deposit.ExpiryTime);
                    Console.WriteLine($"  [REFUNDABLE] Deposit by {depositDetails.Consumer} for {depositDetails.DataAsset.Name} {depositDetails.Deposit.Units} expired on {dto.Date:f}");
                }

                Transaction[] refundTxs = GenerateTxsForRefunds(depositGroup, wallet);
                foreach (Transaction transaction in refundTxs)
                {
                    Console.WriteLine();
                    Console.WriteLine("***************************************");
                    TxDecoder decoder = new TxDecoder();
                    Rlp txRlp = decoder.Encode(transaction);
                    Console.WriteLine(txRlp.Bytes.ToHexString());
                    Console.WriteLine("***************************************");
                }
            }

            Console.ReadLine();
        }

        private static Transaction[] GenerateTxsForRefunds(IGrouping<Address, DepositDetails> depositGroup, DevKeyStoreWallet wallet)
        {
            Console.WriteLine();
            Console.Write($"Provide nonce for {depositGroup.Key}: ");
            int nonce = int.Parse(Console.ReadLine());
            Console.Write($"Provide address to send the refund to: ");
            string hexAddress = Console.ReadLine();            
            Address refundTo = new Address(hexAddress);
            ConsoleUtils consoleUtils= new ConsoleUtils(new ConsoleWrapper());
            
            SecureString securedPassword = consoleUtils.ReadSecret("Provide password: ");

            Console.WriteLine();

            bool unlockSuccessful = wallet.UnlockAccount(depositGroup.Key, securedPassword);
            securedPassword.Dispose();
            
            if (unlockSuccessful)
            {
                Console.WriteLine("Password has been accepted.");
                Console.WriteLine();
                Console.WriteLine($"Great, will generate refund transactions for deposits of {depositGroup.Key} starting with nonce {nonce}. ETH/DAI will be sent to {refundTo}.");
                List<Transaction> transactions = new List<Transaction>(depositGroup.Count());
                foreach (DepositDetails depositDetails in depositGroup)
                {
                    Deposit deposit = depositDetails.Deposit;
                    RefundClaim refundClaim = new RefundClaim(deposit.Id, depositDetails.DataAsset.Id, deposit.Units,
                        deposit.Value, deposit.ExpiryTime, depositDetails.Pepper, depositDetails.DataAsset.Provider.Address, refundTo);
                    UInt256 gasPrice = 20.GWei();

                    AbiEncoder abiEncoder = new AbiEncoder();
                    byte[] txData = abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.ClaimRefundSig, depositDetails.DataAsset.Id, refundClaim.Units, refundClaim.Value, refundClaim.ExpiryTime, refundClaim.Pepper, refundClaim.Provider, depositDetails.Consumer);
                    Transaction transaction = new Transaction();
                    transaction.Value = 0;
                    transaction.Data = txData;
                    transaction.To = new Address("0xb1AD03b75bD9E5AB89968D7a37d99F9dd220796D");
                    transaction.SenderAddress = depositDetails.Consumer;
                    transaction.GasLimit = 100000;
                    transaction.GasPrice = gasPrice;
                    transaction.Nonce = (UInt256) nonce++;
                    wallet.Sign(transaction, ChainId.Mainnet);
                    
                    EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
                    Address recoveredAddress = ecdsa.RecoverAddress(transaction);
                    if (recoveredAddress != transaction.SenderAddress)
                    {
                        Console.WriteLine("Signature failure");
                        return new Transaction[0];
                    }
                    
                    transactions.Add(transaction);
                }

                return transactions.ToArray();
            }
            
            Console.WriteLine("Incorrect password.");
            return new Transaction[0];
        }

        private static async Task<PagedResult<DepositDetails>> LoadDeposits(ILogManager logManager, string dbPath)
        {
            using (var dbProvider = new DbProvider(DbModeHint.Persisted))
            {
                var rocksDbFactory = new RocksDbFactory(DbConfig.Default, logManager, dbPath);
                var dbInitializer = new ConsumerNdmDbInitializer(dbProvider, new NdmConfig(), rocksDbFactory, new MemDbFactory());
                await dbInitializer.InitAsync();
                ConsumerSessionDecoder sessionRlpDecoder = new ConsumerSessionDecoder();
                var sessionRepository =
                    new ConsumerSessionRocksRepository(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.ConsumerSessions), sessionRlpDecoder);
                var depositUnitsCalculator = new DepositUnitsCalculator(sessionRepository, new Timestamper());
                DepositDetailsRocksRepository depositsRepo = new DepositDetailsRocksRepository(dbProvider.GetDb<IDb>(ConsumerNdmDbNames.Deposits), new DepositDetailsDecoder(), depositUnitsCalculator);
                // var deposits = await depositsRepo.BrowseAsync(new GetDeposits());
                var deposits = await depositsRepo.BrowseAsync(new GetDeposits { CurrentBlockTimestamp = Timestamper.Default.UnixTime.SecondsLong, EligibleToRefund = true });
                return deposits;
            }
        }

        private static IKeyStore BuildKeyStore(OneLoggerLogManager logManager)
        {
            KeyStoreConfig keyStoreConfig = new KeyStoreConfig();
            keyStoreConfig.KeyStoreDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            return new FileKeyStore(
                keyStoreConfig,
                new EthereumJsonSerializer(),
                new AesEncrypter(keyStoreConfig, logManager),
                new CryptoRandom(),
                logManager,
                new PrivateKeyStoreIOSettingsProvider(keyStoreConfig));
        }
    }
}
