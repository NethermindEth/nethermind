using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.Eth1Bridge.MockedStart;
using Nethermind.Benchmark.Helpers;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Eth1;

namespace Nethermind.Core2.Cryptography.Benchmark
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class GenesisDepositsBenchmark
    {
        private const int _validatorCount = 512;
        
        [Benchmark(Baseline = true)]
        public Eth1GenesisData Current()
        {
            ChainConstants chainConstants = new ChainConstants();
            CryptographyService cryptographyService = BuildCryptographyService(chainConstants);
            IBeaconChainUtility beaconChainUtility = BuildBeaconChainUtility(chainConstants, cryptographyService);

            QuickStartParameters quickStartParameters = new QuickStartParameters();
            quickStartParameters.ValidatorCount = _validatorCount;
            QuickStartMockEth1GenesisProvider provider = new QuickStartMockEth1GenesisProvider(
                new LimboLogger<QuickStartMockEth1GenesisProvider>(),
                chainConstants,
                Options.Default<GweiValues>(),
                Options.Default<InitialValues>(),
                Options.Default<TimeParameters>(),
                Options.Default<SignatureDomains>(),
                Options.Use(quickStartParameters),
                cryptographyService,
                beaconChainUtility);

            return provider.GetEth1GenesisDataAsync(CancellationToken.None).Result;
        }
        
        [Benchmark]
        public Eth1GenesisData New()
        {
            ChainConstants chainConstants = new ChainConstants();
            CryptographyService cryptographyService = BuildCryptographyService(chainConstants);
            IBeaconChainUtility beaconChainUtility = BuildBeaconChainUtility(chainConstants, cryptographyService);

            QuickStartParameters quickStartParameters = new QuickStartParameters();
            quickStartParameters.ValidatorCount = _validatorCount;
            QuickStartMockEth1GenesisProvider provider = new QuickStartMockEth1GenesisProvider(
                new LimboLogger<QuickStartMockEth1GenesisProvider>(),
                chainConstants,
                Options.Default<GweiValues>(),
                Options.Default<InitialValues>(),
                Options.Default<TimeParameters>(),
                Options.Default<SignatureDomains>(),
                Options.Use(quickStartParameters),
                cryptographyService,
                beaconChainUtility);

            return provider.GetEth1GenesisDataAsync(CancellationToken.None).Result;
        }

        private static CryptographyService BuildCryptographyService(ChainConstants chainConstants)
        {
            CryptographyService cryptographyService = new CryptographyService(
                chainConstants,
                Options.Default<MiscellaneousParameters>(),
                Options.Default<TimeParameters>(),
                Options.Default<StateListLengths>(),
                Options.Default<MaxOperationsPerBlock>());
            return cryptographyService;
        }

        private static IBeaconChainUtility BuildBeaconChainUtility(ChainConstants chainConstants, CryptographyService cryptographyService)
        {
            IBeaconChainUtility beaconChainUtility = new BeaconChainUtility(
                new LimboLogger<BeaconChainUtility>(),
                chainConstants,
                Options.Default<MiscellaneousParameters>(),
                Options.Default<InitialValues>(),
                Options.Default<GweiValues>(),
                Options.Default<TimeParameters>(),
                cryptographyService);
            return beaconChainUtility;
        }
    }
}