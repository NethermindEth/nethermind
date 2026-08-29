// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismModuleTests
{
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
}
