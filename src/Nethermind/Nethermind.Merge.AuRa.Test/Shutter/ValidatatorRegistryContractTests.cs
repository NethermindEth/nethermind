// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Shutter;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

class ValidatorRegistryContractTests
{
    private readonly Address _contractAddress = new("0x000000000000000000000000000000000000beef");
    private readonly ulong _validatorIndex = 99;
    private readonly ulong _nonce = 5;
    private readonly byte[] _encodedRegistrationMessage = [
        ValidatorRegistryContract.validatorRegistryMessageVersion, // VALIDATOR_REGISTRY_MESSAGE_VERSION
        0, 0, 0, 0, 0, 0, 0, 100, // gnosis chain id = 100
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 190, 239, // validator registry address = 0xbeef
        0, 0, 0, 0, 0, 0, 0, 99, // validator index = 99
        0, 0, 0, 0, 0, 0, 0, 5, // nonce = 5
        1 // registration message
    ];
    private readonly BlockHeader _blockHeader = Build.A.EmptyBlockHeader;
    private IReadOnlyTransactionProcessor _transactionProcessor;
    private IAbiEncoder _abiEncoder;
    private ISigner _signer;
    private ITxSender _txSender;
    private ITxSealer _txSealer;
    private IValidatorContract _validatorContract;

    [SetUp]
    public void SetUp()
    {
        _transactionProcessor = Substitute.For<IReadOnlyTransactionProcessor>();
        _abiEncoder = Substitute.For<IAbiEncoder>();
        _signer = Substitute.For<ISigner>();
        _txSender = Substitute.For<ITxSender>();
        _txSealer = Substitute.For<ITxSealer>();
        _validatorContract = Substitute.For<IValidatorContract>();

        // validator index = 2
        _validatorContract.GetValidators(_blockHeader).Returns(new Address[] {Address.Zero, Address.Zero, _contractAddress, Address.Zero});

        // fake execution of contract
        _transactionProcessor.When(x => x.Execute(Arg.Any<Transaction>(), Arg.Any<Evm.BlockExecutionContext>(), Arg.Any<CallOutputTracer>())).Do(x => {
            Transaction transaction = x.Arg<Transaction>();
            CallOutputTracer tracer = x.Arg<CallOutputTracer>();
            tracer.StatusCode = Evm.StatusCode.Success;
            tracer.ReturnValue = new byte[] {10, 100};
        });
    }

    [TearDown]
    public void TearDown() => _transactionProcessor?.Dispose();

    [Test]
    public void Can_encode_registration_message()
    {
        byte[] encodedRegistrationMessage = new ValidatorRegistryContract.Message(_contractAddress, _validatorIndex, _nonce).ComputeRegistrationMessage();
        encodedRegistrationMessage.Should().Equal(_encodedRegistrationMessage);
    }
    
    [Test]
    public void Can_decode_registration_message()
    {
        ValidatorRegistryContract.Message decodedRegistrationMessage = new(_encodedRegistrationMessage);
        decodedRegistrationMessage.Version.Should().Be(ValidatorRegistryContract.validatorRegistryMessageVersion);
        decodedRegistrationMessage.ChainId.Should().Be(BlockchainIds.Gnosis);
        decodedRegistrationMessage.Sender.Should().Be(_contractAddress);
        decodedRegistrationMessage.ValidatorIndex.Should().Be(_validatorIndex);
        decodedRegistrationMessage.Nonce.Should().Be(_nonce);
        Assert.That(decodedRegistrationMessage.IsRegistration);
    }

    [Test]
    public void Can_calculate_validator_index()
    {
        ValidatorRegistryContract contract = new(_transactionProcessor, _abiEncoder, _contractAddress, _signer, _txSender, _txSealer, _validatorContract, _blockHeader);
        ulong validatorIndex = contract.GetValidatorIndex(_blockHeader, _validatorContract);
        validatorIndex.Should().Be(2);
    }

    [Test]
    public void Can_calculate_nonce()
    {
        // ValidatorRegistryContract contract = new(_transactionProcessor, _abiEncoder, _contractAddress, _signer, _txSender, _txSealer, _validatorContract, _blockHeader);
    }

    [Test]
    public void Can_register()
    {
        // ValidatorRegistryContract contract = new(_transactionProcessor, _abiEncoder, _contractAddress, _signer, _txSender, _txSealer, _blockchainBridge, _blockHeader);
    }
}
