// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.P2P;
using Nethermind.Optimism.Rpc;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.TxPool;
using NSubstitute;
using IIPResolver = Nethermind.Network.IIPResolver;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismModuleTests
{
    [Test]
    public void Invalid_cl_p2p_host_reports_the_configuration_key()
    {
        OptimismConfig config = new() { ClP2PHost = "localhost" };
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        using OptimismCLP2P p2p = new(
            Substitute.For<IExecutionEngineManager>(),
            10,
            [],
            config,
            TestItem.AddressA,
            Substitute.For<ITimestamper>(),
            ipResolver,
            LimboLogs.Instance);

        Assert.That(
            async () => await p2p.Run(CancellationToken.None),
            Throws.TypeOf<InvalidConfigurationException>()
                .With.Message.Contains($"{nameof(IOptimismConfig)}.{nameof(IOptimismConfig.ClP2PHost)}"));
    }

    [TestCase(true, TestName = "CL enabled registers the CL startup step")]
    [TestCase(false, TestName = "CL disabled skips the CL startup step")]
    public void ClEnabled_gates_cl_registration(bool clEnabled)
    {
        ChainSpec chainSpec = new()
        {
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters())
        };
        OptimismConfig config = new() { ClEnabled = clEnabled };

        ContainerBuilder builder = new();
        builder.RegisterModule(new OptimismModule(chainSpec, config));
        using IContainer container = builder.Build();

        bool clStepRegistered = container.Resolve<IEnumerable<StepInfo>>()
            .Any(step => step.StepType == typeof(StartOptimismCl));

        Assert.That(clStepRegistered, Is.EqualTo(clEnabled));
    }

    [Test]
    public void Payload_transaction_count_guard_is_disabled()
    {
        ChainSpec chainSpec = new()
        {
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters())
        };

        ContainerBuilder builder = new();
        builder.RegisterModule(new OptimismModule(chainSpec, new OptimismConfig()));
        using IContainer container = builder.Build();

        Assert.That(container.Resolve<Merge.Plugin.Data.PayloadTransactionCountGuard>(), Is.SameAs(Merge.Plugin.Data.PayloadTransactionCountGuard.Disabled));
    }

    // The eth_sendTransaction tests wire their forwarding node the same way.
    [Test]
    public void Node_that_forwards_to_the_sequencer_has_no_transaction_pool()
    {
        ChainSpec chainSpec = new()
        {
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters())
        };
        ContainerBuilder builder = new();
        builder.RegisterInstance(Substitute.For<ITxPool>()).As<ITxPool>();
        builder.RegisterModule(new OptimismModule(chainSpec, new OptimismConfig { SequencerUrl = "http://localhost:8545" }));
        using IContainer container = builder.Build();

        Assert.That(container.Resolve<ITxPool>(), Is.SameAs(NullTxPool.Instance));
    }

    [Test]
    public void Proof_rpc_module_is_decorated_with_deposit_receipt_fields()
    {
        ChainSpec chainSpec = new()
        {
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters())
        };
        ContainerBuilder builder = new();
        builder.RegisterModule(new OptimismModule(chainSpec, new OptimismConfig()));
        builder.RegisterInstance(Substitute.For<IProofRpcModule>()).As<IProofRpcModule>();
        builder.RegisterInstance(Substitute.For<IReceiptFinder>()).Keyed<IReceiptFinder>(IReceiptFinder.RegenerableKey);
        using IContainer container = builder.Build();

        Assert.That(container.Resolve<IProofRpcModule>(), Is.TypeOf<OptimismProofRpcModule>());
    }

    [Test]
    public void Spec_change_validator_preserves_pre_bedrock_legacy_validation()
    {
        const ulong chainId = 10;
        IOptimismReleaseSpec preBedrock = OptimismReleaseSpecSubstitute.Create();
        preBedrock.IsEip1559Enabled.Returns(false);
        IOptimismReleaseSpec postBedrock = OptimismReleaseSpecSubstitute.Create();
        postBedrock.IsEip1559Enabled.Returns(true);
        Transaction transaction = Build.A.Transaction
            .WithGasLimit(0)
            .SignedAndResolved(new EthereumEcdsa(chainId), TestItem.PrivateKeyA)
            .TestObject;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(chainId);
        ChainSpec chainSpec = new()
        {
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters())
        };
        ContainerBuilder builder = new();
        builder.RegisterInstance(new SpecChangeTxValidator(chainId))
            .Keyed<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey);
        builder.RegisterModule(new OptimismModule(chainSpec, new OptimismConfig()));
        builder.RegisterInstance(specProvider).As<ISpecProvider>();
        using IContainer container = builder.Build();
        ITxValidator validator = container.ResolveKeyed<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey);
        string ethereumFingerprint = new SpecChangeTxValidator(chainId).PersistenceFingerprint;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validator, Is.TypeOf<OptimismSpecChangeTxValidator>());
            Assert.That(container.ResolveKeyed<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey), Is.SameAs(validator));
            Assert.That(((ISpecChangeTxValidator)validator).PersistenceFingerprint, Does.Contain(ethereumFingerprint));
            Assert.That(validator.IsWellFormed(transaction, preBedrock).AsBool(), Is.True);
            Assert.That(validator.IsWellFormed(transaction, postBedrock).AsBool(), Is.False);
        }
    }

    [TestCase(TxType.Legacy, true, true, 1ul, false)]
    [TestCase(TxType.DepositTx, true, true, 1ul, false)]
    [TestCase(TxType.Legacy, true, true, 0ul, true)]
    [TestCase(TxType.DepositTx, true, true, 0ul, true)]
    [TestCase(TxType.Legacy, true, false, 1ul, true)]
    [TestCase(TxType.DepositTx, true, false, 1ul, true)]
    [TestCase(TxType.Legacy, false, true, 1ul, true)]
    [TestCase(TxType.DepositTx, false, true, 1ul, false)]
    public void Full_and_delta_validation_respect_optimism_gas_limit_cap(
        TxType transactionType,
        bool isEip1559Enabled,
        bool isEip7825Enabled,
        ulong gasLimitAboveCap,
        bool expected)
    {
        const ulong chainId = 10;
        OptimismReleaseSpec spec = new()
        {
            IsEip1559Enabled = isEip1559Enabled,
            IsEip7825Enabled = isEip7825Enabled
        };
        TransactionBuilder<Transaction> transactionBuilder = Build.A.Transaction
            .WithType(transactionType)
            .WithGasLimit(Eip7825Constants.DefaultTxGasLimitCap + gasLimitAboveCap)
            .WithSenderAddress(TestItem.AddressA);
        Transaction transaction = transactionType == TxType.Legacy
            ? transactionBuilder.SignedAndResolved(new EthereumEcdsa(chainId), TestItem.PrivateKeyA).TestObject
            : transactionBuilder.TestObject;
        ITxValidator fullValidator = transactionType == TxType.DepositTx
            ? Always.Valid
            : new OptimismLegacyTxValidator(chainId);
        OptimismSpecChangeTxValidator specChangeValidator = new(chainId);
        ValidationResult specChangeResult = specChangeValidator.IsWellFormed(transaction, spec);
        ValidationResult admissionResult = fullValidator.IsWellFormed(transaction, spec);

        if (admissionResult)
        {
            admissionResult = specChangeValidator.IsWellFormedAfterFullValidation(transaction, spec);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(specChangeResult.AsBool, Is.EqualTo(expected));
            Assert.That(admissionResult.AsBool, Is.EqualTo(expected));
        }
    }

    [TestCase(false, ulong.MaxValue, null)]
    [TestCase(true, ulong.MaxValue - 1, null)]
    [TestCase(true, ulong.MaxValue, TxErrorMessages.NonceTooHigh)]
    public void Legacy_validation_respects_eip2681_nonce_cap_after_bedrock(
        bool isEip1559Enabled,
        ulong nonce,
        string? expectedError)
    {
        const ulong chainId = 10;
        OptimismReleaseSpec spec = new() { IsEip1559Enabled = isEip1559Enabled };
        Transaction transaction = Build.A.Transaction
            .WithNonce(nonce)
            .SignedAndResolved(new EthereumEcdsa(chainId), TestItem.PrivateKeyA)
            .TestObject;

        ValidationResult result = new OptimismLegacyTxValidator(chainId).IsWellFormed(transaction, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AsBool, Is.EqualTo(expectedError is null));
            Assert.That(result.Error, Is.EqualTo(expectedError));
        }
    }

    [Test]
    public void Full_validation_prioritizes_sender_independent_errors_over_intrinsic_gas()
    {
        OptimismReleaseSpec spec = new() { IsEip1559Enabled = true };
        Transaction transaction = Build.A.Transaction
            .WithGasLimit(0)
            .TestObject;

        ValidationResult result = new OptimismLegacyTxValidator(10).IsWellFormed(transaction, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AsBool, Is.False);
            Assert.That(result.IsIntrinsicGasError, Is.False);
            Assert.That(result.Error, Is.EqualTo(TxErrorMessages.InvalidTxSignature));
        }
    }

    // EIP-8288: the recursive STARK check is a decorator over whichever block validator a plugin registers.
    [Test]
    public void Recursive_stark_check_applies_over_the_optimism_block_validator([Values] bool emptyProof)
    {
        IOptimismReleaseSpec spec = OptimismReleaseSpecSubstitute.Create();
        spec.IsEip8288Enabled.Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        ChainSpec chainSpec = new()
        {
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters())
        };
        ContainerBuilder builder = new();
        builder.RegisterModule(new BlockProcessingModule(new InitConfig(), new BlocksConfig()));
        builder.RegisterModule(new OptimismModule(chainSpec, new OptimismConfig()));
        builder.RegisterInstance(specProvider).As<ISpecProvider>();
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(Always.Valid).As<IHeaderValidator>();
        builder.RegisterInstance(Substitute.For<IBlockTree>()).As<IBlockTree>().As<IBlockFinder>();
        builder.RegisterInstance(Substitute.For<IDependencyProofStore>()).As<IDependencyProofStore>();
        builder.RegisterInstance(Substitute.For<IPoSSwitcher>()).As<IPoSSwitcher>();
        builder.RegisterInstance(Substitute.For<ILeanProofVerifier>()).As<ILeanProofVerifier>();
        using IContainer container = builder.Build();

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).WithWithdrawals(null).TestObject;
        block.StarkProof = emptyProof ? [] : [1];
        block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(block.StarkProof), new Hash256(Eip8288Dependencies.ComputeDepsHash([])));
        bool isValid = container.Resolve<IBlockValidator>().ValidateSuggestedBlock(block, parent, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(isValid, Is.EqualTo(emptyProof), error);
            Assert.That(error, Is.EqualTo(emptyProof ? null : BlockErrorMessages.InvalidRecursiveStark));
        }
    }
}
