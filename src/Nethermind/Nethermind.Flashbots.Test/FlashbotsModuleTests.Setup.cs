// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Flashbots.Handlers;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;

namespace Nethermind.Flashbots.Test;

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

        public UInt256 BaseInitialFee = 1000000000;
        public TestKeyAndAddress()
        {
            TestAddr = PrivateKey.Address;
            TestValidatorAddr = TestValidatorKey.Address;
            TestBuilderAddr = TestBuilderKey.Address;
        }
    }

    protected static async Task<EngineModuleTests.MergeTestBlockchain> CreateBlockChain(
        IReleaseSpec? releaseSpec = null)
    => await new EngineModuleTests.MergeTestBlockchain().Build(new TestSingleReleaseSpecProvider(releaseSpec ?? London.Instance));

    private IFlashbotsRpcModule CreateFlashbotsModule(EngineModuleTests.MergeTestBlockchain chain)
    {
        return new FlashbotsRpcModule(
            new ValidateSubmissionHandler(
                chain.HeaderValidator,
                chain.BlockTree,
                chain.BlockValidator,
                chain.ReadOnlyTxProcessingEnvFactory,
                chain.LogManager,
                chain.SpecProvider,
                new FlashbotsConfig(),
                chain.EthereumEcdsa
            )
        );
    }
}
