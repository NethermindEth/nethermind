// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Flashbots;
using Nethermind.Flashbots.Handlers;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Flasbots.Test;

public partial class FlashbotsModuleTests
{
    TestKeyAndAddress? TestKeysAndAddress;

    [SetUp]
    public void SetUp()
    {
        TestKeysAndAddress = new TestKeyAndAddress();
    }

    internal class TestKeyAndAddress
    {
        public PrivateKey PrivateKey = new PrivateKey("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        public Address TestAddr;

        public PrivateKey TestValidatorKey = new PrivateKey("28c3cd61b687fdd03488e167a5d84f50269df2a4c29a2cfb1390903aa775c5d0");
        public Address TestValidatorAddr;

        public PrivateKey TestBuilderKey = new PrivateKey("0bfbbbc68fefd990e61ba645efb84e0a62e94d5fff02c9b1da8eb45fea32b4e0");
        public Address TestBuilderAddr;

        public UInt256 TestBalance = UInt256.Parse("2000000000000000000");
        public byte[] logCode = Bytes.FromHexString("60606040525b7f24ec1d3ff24c2f6ff210738839dbc339cd45a5294d85c79361016243157aae7b60405180905060405180910390a15b600a8060416000396000f360606040526008565b00");

        public UInt256 BaseInitialFee  = 1000000000;
        public TestKeyAndAddress()
        {
            TestAddr = PrivateKey.Address;
            TestValidatorAddr = TestValidatorKey.Address;
            TestBuilderAddr = TestBuilderKey.Address;
        }
    }

    protected virtual MergeTestBlockChain CreateBaseBlockChain(
        IFlashbotsConfig flashbotsConfig,
        ILogManager? logManager = null)
    {
        return new MergeTestBlockChain(flashbotsConfig, logManager);
    }

    protected async Task<MergeTestBlockChain> CreateBlockChain(
        IReleaseSpec? releaseSpec = null,
        IFlashbotsConfig? flashbotsConfig = null,
        ILogManager? logManager = null)
    => await CreateBaseBlockChain(flashbotsConfig ?? new FlashbotsConfig(), logManager).Build(new TestSingleReleaseSpecProvider(releaseSpec ?? London.Instance));

    private IFlashbotsRpcModule CreateFlashbotsModule(MergeTestBlockChain chain, ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv)
    {
        return new FlashbotsRpcModule(
            new ValidateSubmissionHandler(
                chain.HeaderValidator,
                chain.BlockValidator,
                readOnlyTxProcessingEnv,
                chain.FlashbotsConfig
            )
        );
    }

    public class MergeTestBlockChain : TestBlockchain
    {
        public IFlashbotsConfig FlashbotsConfig;

        public IMergeConfig MergeConfig;

        public IWithdrawalProcessor? WithdrawalProcessor { get; set; }

        public ReadOnlyTxProcessingEnv ReadOnlyTxProcessingEnv { get; set; }

        public MergeTestBlockChain(IFlashbotsConfig flashbotsConfig, ILogManager? logManager = null)
        {
            FlashbotsConfig = flashbotsConfig;
            MergeConfig = new MergeConfig() { TerminalTotalDifficulty = "0" };
            LogManager = logManager ?? LogManager;
        }

        public sealed override ILogManager LogManager { get; set; } = LimboLogs.Instance;

        public ReadOnlyTxProcessingEnv CreateReadOnlyTxProcessingEnv()
        {
            ReadOnlyTxProcessingEnv = new ReadOnlyTxProcessingEnv(
                WorldStateManager,
                BlockTree,
                SpecProvider,
                LogManager
            );
            return ReadOnlyTxProcessingEnv;
        }

        protected override IBlockProcessor CreateBlockProcessor()
        {
            BlockValidator = CreateBlockValidator();
            WithdrawalProcessor = new WithdrawalProcessor(State, LogManager);
            IBlockProcessor prcessor = new BlockProcessor(
                SpecProvider,
                BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                State,
                ReceiptStorage,
                TxProcessor,
                new BeaconBlockRootHandler(TxProcessor, State),
                new BlockhashStore(SpecProvider, State),
                LogManager,
                WithdrawalProcessor
            );

            return prcessor;
        }

        protected IBlockValidator CreateBlockValidator()
        {
            PoSSwitcher = new PoSSwitcher(MergeConfig, SyncConfig.Default, new MemDb(), BlockTree, SpecProvider, new ChainSpec() { Genesis = Core.Test.Builders.Build.A.Block.WithDifficulty(0).TestObject }, LogManager);
            ISealValidator SealValidator = new MergeSealValidator(PoSSwitcher, Always.Valid);
            HeaderValidator = new MergeHeaderValidator(
                PoSSwitcher,
                new HeaderValidator(BlockTree, SealValidator, SpecProvider, LogManager),
                BlockTree,
                SpecProvider,
                SealValidator,
                LogManager
            );

            return new BlockValidator(
                new TxValidator(SpecProvider.ChainId),
                HeaderValidator,
                Always.Valid,
                SpecProvider,
                LogManager
            );
        }

        protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null, bool addBlockOnStart = true)
        {
            TestBlockchain chain = await base.Build(specProvider, initialValues);
            return chain;
        }

        public async Task<MergeTestBlockChain> Build(ISpecProvider? specProvider = null) =>
            (MergeTestBlockChain)await Build(specProvider, null);

    }
}
