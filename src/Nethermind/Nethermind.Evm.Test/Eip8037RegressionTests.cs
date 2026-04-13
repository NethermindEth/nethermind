// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037RegressionTests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    /// <summary>
    /// When a nested CREATE's child frame has too little regular gas to cover both
    /// the regular code deposit cost AND the state-gas spill, the CREATE must fail.
    ///
    /// Gas budget (1-byte deployed contract, child state reservoir = 0):
    ///   regularDepositCost  = 6  (CodeDepositRegularPerWord x 1 word)
    ///   stateDepositCost    = 1174 (CostPerStateByte x 1 byte)
    ///   stateSpill          = 1174 (entire stateDepositCost spills into regular gas)
    ///   total regular needed = 6 + 1174 = 1180
    ///
    /// Child ends with 1175 regular gas after init code.
    /// Without the fix, the pre-check passes (1175 >= 6 and 1175 >= 1174) and the
    /// charge runs on the merged parent+child pool, silently borrowing parent gas.
    /// </summary>
    [Test]
    public void Eip8037_nested_create_code_deposit_must_not_borrow_parent_regular_gas()
    {
        // Init code: deploys 1 byte of zeros from memory
        // PUSH1 1, PUSH1 0, RETURN = 5 bytes, costs 9 gas (3+3+3 memory expansion)
        byte[] initCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        // Factory code: CREATE(value=0, initCode), then RETURN the result (address or 0)
        byte[] factoryCode = Prepare.EvmCode
            .Create(initCode, UInt256.Zero)
            // Stack: [address or 0]
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        // Gas calculation:
        //   Intrinsic (CALL to existing account): 21000
        //   Factory pre-CREATE opcodes: 21 gas
        //   CREATE opcode costs:
        //     CreateRegular(9000) + InitCodeWord(2) = 9002 regular
        //     CreateState(131488) -> spills entirely to regular (factory has 0 state reservoir)
        //     Total: 140490 regular
        //   Remaining after CREATE costs: 1202
        //   63/64 rule: callGas = 1202 - floor(1202/64) = 1184, factory retains 18
        //   Child: 1184 gas -> 9 for init code -> 1175 remaining for code deposit
        //   Factory post-CREATE: 12 gas (PUSH, MSTORE, PUSH, PUSH, RETURN)
        //   Total: 21000 + 21 + 140490 + 1202 = 162713
        long gasLimit = 162713;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, factoryCode);

        // Transaction succeeds (factory runs fine), but the nested CREATE must fail
        // because the child can't afford the code deposit from its own gas alone.
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "Factory execution should succeed");

        // CREATE result: 0 = failure (returned in the 32-byte output)
        byte[] returnData = tracer.ReturnValue;
        Assert.That(returnData.IsZero(), Is.True,
            "Nested CREATE should fail: child has 1175 gas but needs 1180 for code deposit (6 regular + 1174 state spill)");
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.LessThan(tracer.GasConsumedResult.BlockStateGas));
    }

    [Test]
    public void Eip8037_nested_create_code_deposit_failure_must_keep_create_state_in_block_state_gas()
    {
        byte[] childInitCode = Prepare.EvmCode
            .PushData(33_000)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        byte[] code = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 5_000_000, code);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState));
    }

    /// <summary>
    /// When a top-level CREATE tx's initcode performs a CALL to a new account and the
    /// later code deposit fails, all state gas should be discarded from block-state
    /// accounting.
    ///
    /// The exceptional halt restores the CREATE state reservoir together with any
    /// initcode state gas, so block_state must return to zero.
    /// </summary>
    [Test]
    public void Eip8037_code_deposit_failure_must_keep_initcode_state_gas_in_state_dimension()
    {
        // Initcode:
        //   1) CALL a dead account with value=1 - charges NewAccountState (131,488)
        //   2) RETURN 33,000 bytes - exceeds MaxCodeSizeEip7954 (32,768), triggers deposit failure
        byte[] initCode = Prepare.EvmCode
            .CallWithValue(TestItem.AddressC, 20_000, UInt256.One)
            .PushData(33_000)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        long gasLimit = 1_000_000;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, initCode, value: 1, blockGasLimit: 50_000_000);
        transaction.To = null;
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "CREATE tx with oversized code return should fail");
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas,
            Is.EqualTo(gasLimit - GasCostOf.CreateState - GasCostOf.NewAccountState));
        Assert.That(tracer.GasConsumedResult.BlockStateGas,
            Is.EqualTo(GasCostOf.CreateState + GasCostOf.NewAccountState));
        Assert.That(TestState.AccountExists(TestItem.AddressC), Is.False,
            "The initcode CALL target must be reverted together with its state gas.");
    }

    [Test]
    public void Eip8037_top_level_create_collision_must_not_count_paid_execution_gas_as_block_regular_gas()
    {
        byte[] initCode = Prepare.EvmCode
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Done;

        const long gasLimit = 600_000;
        Address collisionAddress = ContractAddress.From(Sender, 0);

        TestState.CreateAccount(Sender, 100.Ether);
        TestState.CreateAccount(collisionAddress, 0, 1);
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);

        Transaction transaction = Build.A.Transaction
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithValue(0)
            .WithNonce(0)
            .WithCode(initCode)
            .SignedAndResolved(SenderKey)
            .TestObject;
        Block block = BuildBlock(Activation, SenderRecipientAndMiner.Default, transaction, blockGasLimit: 50_000_000);

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit),
            "The sender still pays the full transaction gas for the collision.");
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.LessThan(tracer.GasConsumedResult.BlockStateGas),
            "Block regular gas should only contain intrinsic regular/floor gas, so intrinsic state gas dominates.");
    }

    [Test]
    public void Eip8037_oversized_initcode_create_must_keep_create_state_out_of_block_state_gas()
    {
        byte[] createFromCalldataCode = Prepare.EvmCode
            .CALLDATASIZE()
            .CALLDATACOPY(0, 0)
            .CALLDATASIZE()
            .CREATEx(1, 0, 0, null)
            .Op(Instruction.STOP)
            .Done;

        byte[] oversizedInitCode = new byte[(int)Spec.MaxInitCodeSize + 1];
        const long gasLimit = 1_000_000;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, createFromCalldataCode, oversizedInitCode, UInt256.Zero);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
    }

    [TestCase(false, TestName = "Eip8037_create_memory_oog_must_not_charge_create_state_CREATE")]
    [TestCase(true, TestName = "Eip8037_create_memory_oog_must_not_charge_create_state_CREATE2")]
    public void Eip8037_create_memory_oog_must_not_charge_create_state(bool create2)
    {
        long gasLimit = Eip7825Constants.DefaultTxGasLimitCap;
        const long highOffset = 0x1_0000_0000L;

        Prepare codeBuilder = Prepare.EvmCode;
        if (create2)
        {
            codeBuilder.PushData(0x5A17);
        }

        byte[] code = codeBuilder
            .PushData(5)
            .PushData(highOffset)
            .PushData(0)
            .Op(create2 ? Instruction.CREATE2 : Instruction.CREATE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code, blockGasLimit: 50_000_000);
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
    }

    [TestCase(false, TestName = "Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce_CREATE")]
    [TestCase(true, TestName = "Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce_CREATE2")]
    public void Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce(bool create2)
    {
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        byte[] createAttemptCode = create2
            ? Prepare.EvmCode.Create2(childInitCode, [0x01], UInt256.Zero).Op(Instruction.STOP).Done
            : Prepare.EvmCode.Create(childInitCode, UInt256.Zero).Op(Instruction.STOP).Done;
        Address createdAddress = create2
            ? ContractAddress.From(TestItem.AddressC, [0x01], childInitCode)
            : ContractAddress.From(TestItem.AddressC, 0);

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createAttemptCode, SpecProvider.GenesisSpec);

        byte[] outerCode = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 50_000)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 200_000, outerCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(UInt256.Zero));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [TestCase(false, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE2")]
    public void Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas(bool create2)
    {
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        byte[] createAttemptCode = create2
            ? Prepare.EvmCode.Create2(childInitCode, [0x01], UInt256.Zero).Op(Instruction.STOP).Done
            : Prepare.EvmCode.Create(childInitCode, UInt256.Zero).Op(Instruction.STOP).Done;
        Address createdAddress = create2
            ? ContractAddress.From(TestItem.AddressC, [0x01], childInitCode)
            : ContractAddress.From(TestItem.AddressC, 0);

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createAttemptCode, SpecProvider.GenesisSpec);
        TestState.Set(new StorageCell(Recipient, 0), [0xDE, 0xAD]);

        byte[] outerCode = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 200_000)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 500_000, outerCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(259_698));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(226_930));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }));
        Assert.That(TestState.Get(new StorageCell(Recipient, 1)).ToArray(), Is.EqualTo(new byte[] { 1 }));
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(UInt256.Zero));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [Test]
    public void Eip8037_top_level_exceptional_halt_must_count_state_spill_in_block_regular()
    {
        Prepare codeBuilder = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE);

        for (int i = 0; i < 1_025; i++)
        {
            codeBuilder.Op(Instruction.PUSH0);
        }

        const long gasLimit = 100_000;
        byte[] code = codeBuilder.Done;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StackOverflow)));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        AssertStorage(new StorageCell(Recipient, 0), UInt256.Zero);
    }

    [Test]
    public void Eip8037_block_validation_must_not_use_header_max_gas_used_as_remaining_tx_budget()
    {
        byte[] stateHeavyCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        const long blockGasLimit = 500_000;
        (Block block, Transaction firstTx) = PrepareTx(Activation, 100_000, stateHeavyCode, value: 0, blockGasLimit: blockGasLimit);

        TestAllTracerWithOutput firstTracer = CreateTracer();
        TransactionResult firstResult = _processor.Execute(
            firstTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            firstTracer);

        Assert.That(firstResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(firstTracer.GasConsumedResult.BlockStateGas, Is.GreaterThan(firstTx.BlockGasUsed),
            "The first tx should be state-gas dominated so header.gasUsed tracks the state dimension.");
        Assert.That(block.Header.RegularGasUsed, Is.EqualTo(firstTracer.GasConsumedResult.EffectiveBlockGas));
        Assert.That(block.Header.StateGasUsed, Is.EqualTo(firstTracer.GasConsumedResult.BlockStateGas));
        Assert.That(block.Header.GasUsed, Is.EqualTo(firstTracer.GasConsumedResult.BlockStateGas));

        long legacyRemaining = block.Header.GasLimit - block.Header.GasUsed;
        long secondTxGasLimit = legacyRemaining + 1;

        SenderRecipientAndMiner secondParticipants = new()
        {
            SenderKey = TestItem.PrivateKeyC,
            RecipientKey = TestItem.PrivateKeyD,
            MinerKey = TestItem.PrivateKeyD,
        };
        (_, Transaction secondTx) = PrepareTx(
            Activation,
            secondTxGasLimit,
            null,
            senderRecipientAndMiner: secondParticipants,
            value: 0,
            blockGasLimit: blockGasLimit);

        TestAllTracerWithOutput secondTracer = CreateTracer();
        TransactionResult secondResult = _processor.Execute(
            secondTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            secondTracer);

        Assert.That(secondTxGasLimit, Is.GreaterThan(legacyRemaining));
        Assert.That(secondResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(secondTracer.GasConsumedResult.EffectiveBlockGas, Is.LessThan(legacyRemaining));
        Assert.That(block.Header.GasUsed, Is.LessThanOrEqualTo(block.Header.GasLimit));
    }

    /// <summary>
    /// A child CALL that runs out of gas during SSTORE must not spill state gas into the
    /// parent frame's reservoir. If it does, the parent can incorrectly complete its own
    /// SSTORE with gas that should have been burned by the child halt.
    /// </summary>
    [Test]
    public void Eip8037_failed_child_sstore_must_not_inflate_parent_state_reservoir()
    {
        byte[] childCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        byte[] parentCode = Prepare.EvmCode
            .Call(TestItem.AddressC, 40_000)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 70_000, parentCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "The parent SSTORE should run out of gas once the child CALL burns its own failed SSTORE gas.");
        Assert.That(tracer.Error, Is.EqualTo("OutOfGas"));
    }

    [Test]
    public void Eip8037_reverted_child_sstores_must_still_count_toward_block_state_gas()
    {
        byte[] childCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(2)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.INVALID)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        byte[] parentCode = Prepare.EvmCode
            .Call(TestItem.AddressC, 300_000)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 500_000, parentCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success),
            "The parent CALL returns failure, but the parent frame continues successfully.");
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(2 * GasCostOf.SSetState));
        AssertStorage(new StorageCell(TestItem.AddressC, 0), UInt256.Zero);
        AssertStorage(new StorageCell(TestItem.AddressC, 1), UInt256.Zero);
    }

    [Test]
    public void Eip8037_set_code_invalid_sstores_must_count_reverted_state_gas()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey authority = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;

        byte[] delegatedCode = Prepare.EvmCode
            .Op(Instruction.ORIGIN)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.CALLER)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.CALLVALUE)
            .PushData(2)
            .Op(Instruction.SSTORE)
            .Op(Instruction.INVALID)
            .Done;

        TestState.CreateAccount(sender.Address, 10.Ether);
        TestState.CreateAccount(authority.Address, 1);
        TestState.CreateAccount(codeSource, 0);
        TestState.InsertCode(codeSource, delegatedCode, Spec);
        TestState.Commit(Spec);
        TestState.CommitTree(0);

        IEthereumEcdsa ecdsa = new EthereumEcdsa(SpecProvider.ChainId);
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithChainId(SpecProvider.ChainId)
            .WithNonce(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(7)
            .WithGasLimit(500_000)
            .WithValue(0)
            .To(authority.Address)
            .WithAuthorizationCode(ecdsa.Sign(authority, 0, codeSource, 0))
            .SignedAndResolved(ecdsa, sender, true)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(Activation.BlockNumber)
            .WithTimestamp(Activation.Timestamp ?? 0)
            .WithTransactions(tx)
            .WithGasLimit(120_000_000)
            .WithBaseFeePerGas(7)
            .WithBeneficiary(TestItem.AddressD)
            .WithParentBeaconBlockRoot(TestItem.KeccakG)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithSlotNumber(0)
            .TestObject;

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(
            tx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        long intrinsicAuthState = GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState;
        long expectedBlockRegular = tx.GasLimit - intrinsicAuthState;
        long expectedBlockState = GasCostOf.PerAuthBaseState + 2 * GasCostOf.SSetState;
        long expectedSpentGas = expectedBlockRegular + expectedBlockState;

        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(expectedSpentGas), tracer.GasConsumedResult.ToString());
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(expectedBlockRegular), tracer.GasConsumedResult.ToString());
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(expectedBlockState), tracer.GasConsumedResult.ToString());
        Assert.That(block.Header.GasUsed, Is.EqualTo(expectedBlockRegular));
        AssertStorage(new StorageCell(authority.Address, 0), UInt256.Zero);
        AssertStorage(new StorageCell(authority.Address, 1), UInt256.Zero);
        Assert.That(TestState.GetCode(authority.Address), Is.Not.Empty);
        Assert.That(TestState.GetNonce(authority.Address), Is.EqualTo(UInt256.One));
    }
}
