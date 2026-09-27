// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8151: ecRecover returns the recovered address only when its raw code is empty or an EIP-7702 delegation
/// designator, and charges the EIP-2929 access cost for that address.
/// </summary>
/// <remarks>
/// Transactions run untraced through a <see cref="PrecompileCachedCodeInfoRepository"/>, as block processing does, so
/// the inline STATICCALL path, access-list rollback and the precompile result cache all behave as in production.
/// Each measured operation stores <c>gas before</c>, its result, <c>gas after</c>, the return data size and the output
/// word into consecutive storage slots of the called contract.
/// </remarks>
public class Eip8151Tests : VirtualMachineTestsBase
{
    private const ulong TxGasLimit = 1_000_000;
    private const long PrecompileGasLimit = 100_000;
    private const int OutputOffset = 128;
    private const int SlotsPerMeasurement = 5;
    private const ulong EcRecoverBaseCost = 3000;

    private static readonly PrivateKey SignerKey = TestItem.PrivateKeyC;
    private static readonly Address Signer = SignerKey.Address;
    private static readonly Address Helper = TestItem.AddressE;
    private static readonly Address MovedPrecompileTarget = TestItem.AddressF;
    private static readonly byte[] ValidInput = CreateInput(SignerKey);
    private static readonly byte[] UnrecoverableInput = CreateUnrecoverableInput();
    private static readonly byte[] Sentinel = Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
    private static readonly byte[] ContractCode = Prepare.EvmCode.PushData(0).Op(Instruction.STOP).Done;

    private OverridableReleaseSpec _spec = null!;
    private ISpecProvider _specProvider = null!;
    private TracedAccessWorldState _tracedState = null!;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;
    protected override ISpecProvider SpecProvider => _specProvider;

    [SetUp]
    public override void Setup()
    {
        // Opcode tables are cached per spec instance, so a test that flips a repricing flag needs its own.
        _spec = new OverridableReleaseSpec(Prague.Instance);
        _specProvider = new TestSpecProvider(_spec);
        base.Setup();
        _spec.IsEip8151Enabled = true;
        CreateProcessor(parallel: false);
    }

    private static IEnumerable<TestCaseData> RecoveredAccountCases()
    {
        (string name, byte[]? code, bool permitted)[] accounts =
        [
            ("absent account", null, true),
            ("account without code", [], true),
            ("EIP-7702 delegation", Bytes.FromHexString("0xef0100" + new string('4', 40)), true),
            ("contract", ContractCode, false),
            ("22-byte delegation prefix", Bytes.FromHexString("0xef0100" + new string('4', 38)), false),
            ("24-byte delegation prefix", Bytes.FromHexString("0xef0100" + new string('4', 42)), false),
            ("23-byte code with another version", Bytes.FromHexString("0xef0101" + new string('4', 40)), false),
        ];

        foreach (bool eip8151Enabled in (bool[])[true, false])
        {
            foreach (Instruction callOpcode in (Instruction[])[Instruction.CALL, Instruction.STATICCALL, Instruction.DELEGATECALL, Instruction.CALLCODE])
            {
                foreach ((string name, byte[]? code, bool permitted) in accounts)
                {
                    yield return new TestCaseData(eip8151Enabled, callOpcode, code, permitted || !eip8151Enabled)
                        .SetName($"{nameof(Recovered_address_is_returned_only_for_empty_or_delegation_code)}({(eip8151Enabled ? "EIP-8151" : "pre-EIP-8151")}, {callOpcode}, {name})");
                }
            }
        }
    }

    [TestCaseSource(nameof(RecoveredAccountCases))]
    public void Recovered_address_is_returned_only_for_empty_or_delegation_code(bool eip8151Enabled, Instruction callOpcode, byte[]? signerCode, bool returnsAddress)
    {
        _spec.IsEip8151Enabled = eip8151Enabled;
        DeploySigner(signerCode);

        Run(MeasureEcRecover(Prepare.EvmCode, 0, callOpcode, PrecompileGasLimit, ValidInput).Done);

        Measurement call = Read(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Result, Is.EqualTo(UInt256.One), "success");
            Assert.That(call.Output, Is.EqualTo(returnsAddress ? AsWord(Signer) : UInt256.Zero), "output");
            Assert.That(call.ReturnDataSize, Is.EqualTo((UInt256)32), "return data size");
            Assert.That(PrecompileCost(call, callOpcode), Is.EqualTo(eip8151Enabled ? EcRecoverBaseCost + GasCostOf.ColdAccountAccess : EcRecoverBaseCost), "gas");
        }
    }

    [Test]
    public void Failed_recovery_charges_the_base_cost_only(
        [Values] bool eip8151Enabled,
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction callOpcode)
    {
        _spec.IsEip8151Enabled = eip8151Enabled;

        Run(MeasureEcRecover(Prepare.EvmCode, 0, callOpcode, PrecompileGasLimit, UnrecoverableInput).Done);

        Measurement call = Read(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Result, Is.EqualTo(UInt256.One), "success");
            Assert.That(call.ReturnDataSize, Is.EqualTo(eip8151Enabled ? (UInt256)32 : UInt256.Zero), "return data size");
            Assert.That(call.Output, Is.EqualTo(eip8151Enabled ? UInt256.Zero : new UInt256(Sentinel, isBigEndian: true)), "output");
            Assert.That(PrecompileCost(call, callOpcode), Is.EqualTo(EcRecoverBaseCost), "gas");
        }
    }

    [Test]
    public void Recovery_warms_the_recovered_address_for_the_rest_of_the_transaction(
        [Values] bool eip8151Enabled,
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction callOpcode,
        [Values] bool eip8038Enabled)
    {
        _spec.IsEip8151Enabled = eip8151Enabled;
        _spec.IsEip8038Enabled = eip8038Enabled;
        ulong coldAccountAccess = eip8038Enabled ? Eip8038Constants.ColdAccountAccess : GasCostOf.ColdAccountAccess;
        Prepare code = MeasureEcRecover(Prepare.EvmCode, 0, callOpcode, PrecompileGasLimit, ValidInput);
        code = MeasureEcRecover(code, 1, callOpcode, PrecompileGasLimit, ValidInput);

        Run(MeasureBalance(code, 2, Signer).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(0).Output, Is.EqualTo(AsWord(Signer)), "first output");
            Assert.That(Read(1).Output, Is.EqualTo(AsWord(Signer)), "second output");
            Assert.That(PrecompileCost(Read(0), callOpcode), Is.EqualTo(eip8151Enabled ? EcRecoverBaseCost + coldAccountAccess : EcRecoverBaseCost), "first recovery gas");
            Assert.That(PrecompileCost(Read(1), callOpcode), Is.EqualTo(eip8151Enabled ? EcRecoverBaseCost + GasCostOf.WarmStateRead : EcRecoverBaseCost), "second recovery gas");
            Assert.That(BalanceCost(Read(2)), Is.EqualTo(eip8151Enabled ? GasCostOf.WarmStateRead : coldAccountAccess), "later BALANCE gas");
        }
    }

    [TestCase(true, GasCostOf.ColdAccountAccess, TestName = "Warming_follows_the_recovering_frame(reverted frame leaves the address cold)")]
    [TestCase(false, GasCostOf.WarmStateRead, TestName = "Warming_follows_the_recovering_frame(committed frame leaves the address warm)")]
    public void Warming_follows_the_recovering_frame(bool revert, ulong expectedBalanceCost)
    {
        Prepare helperCode = Prepare.EvmCode
            .StoreDataInMemory(0, ValidInput)
            .PushData(32)
            .PushData(OutputOffset)
            .PushData(ValidInput.Length)
            .PushData(0)
            .PushData(PrecompiledAddresses.ECRecover.Value)
            .PushData(PrecompileGasLimit)
            .Op(Instruction.STATICCALL)
            .Op(Instruction.POP);
        helperCode = revert ? helperCode.PushData(0).PushData(0).Op(Instruction.REVERT) : helperCode.Op(Instruction.STOP);
        TestState.CreateAccount(Helper, UInt256.Zero);
        TestState.InsertCode(Helper, helperCode.Done, _spec);

        Run(MeasureBalance(Prepare.EvmCode.Call(Helper, 200_000).Op(Instruction.POP), 0, Signer).Done);

        Assert.That(BalanceCost(Read(0)), Is.EqualTo(expectedBalanceCost));
    }

    [Test]
    public void Access_cost_is_charged_before_the_recovered_account_is_read(
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction callOpcode,
        [Values] bool enoughGas)
    {
        long gasLimit = (long)(EcRecoverBaseCost + GasCostOf.ColdAccountAccess) - (enoughGas ? 0 : 1);

        Run(MeasureEcRecover(Prepare.EvmCode, 0, callOpcode, gasLimit, ValidInput).Done);

        Measurement call = Read(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Result, Is.EqualTo(enoughGas ? UInt256.One : UInt256.Zero), "success");
            Assert.That(PrecompileCost(call, callOpcode), Is.EqualTo((ulong)gasLimit), "gas");
            Assert.That(_tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(Signer), enoughGas ? Is.Not.Null : Is.Null, "EIP-7928 entry");
        }
    }

    [Test]
    public void Running_out_of_gas_for_the_access_cost_leaves_the_address_cold(
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction callOpcode)
    {
        Prepare code = MeasureEcRecover(Prepare.EvmCode, 0, callOpcode, (long)(EcRecoverBaseCost + GasCostOf.ColdAccountAccess) - 1, ValidInput);

        Run(MeasureBalance(code, 1, Signer).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(0).Result, Is.EqualTo(UInt256.Zero), "success");
            Assert.That(BalanceCost(Read(1)), Is.EqualTo(GasCostOf.ColdAccountAccess), "later BALANCE gas");
        }
    }

    [Test]
    public void Recovered_address_is_recorded_in_the_block_access_list([Values] bool eip8151Enabled, [Values] bool parallel)
    {
        _spec.IsEip8151Enabled = eip8151Enabled;
        CreateProcessor(parallel);

        Run(MeasureEcRecover(Prepare.EvmCode, 0, Instruction.STATICCALL, PrecompileGasLimit, ValidInput).Done);

        Assert.That(_tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(Signer), eip8151Enabled ? Is.Not.Null : Is.Null);
    }

    [Test]
    public void Cached_recovery_does_not_return_an_address_whose_account_gained_code()
    {
        byte[] code = MeasureEcRecover(Prepare.EvmCode, 0, Instruction.STATICCALL, PrecompileGasLimit, ValidInput).Done;
        Run(code);
        Assert.That(Read(0).Output, Is.EqualTo(AsWord(Signer)), "output before the account gains code");

        DeploySigner(ContractCode);
        long recoveriesBefore = Precompiles.Metrics.ECRecoverPrecompile;
        Run(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Precompiles.Metrics.ECRecoverPrecompile, Is.EqualTo(recoveriesBefore), "second recovery is served from the precompile result cache");
            Assert.That(Read(0).Output, Is.EqualTo(UInt256.Zero), "output after the account gains code");
        }
    }

    [Test]
    public void Transaction_to_ecrecover_applies_the_restriction([Values] bool eip8151Enabled)
    {
        _spec.IsEip8151Enabled = eip8151Enabled;
        DeploySigner(ContractCode);
        Transaction tx = Build.A.Transaction
            .To(PrecompiledAddresses.ECRecover.Value)
            .WithData(ValidInput)
            .WithGasLimit(TxGasLimit)
            .WithNonce(TestState.GetNonce(Sender))
            .SignedAndResolved(new EthereumEcdsa(SpecProvider.ChainId), SenderKey)
            .TestObject;

        TestAllTracerWithOutput tracer = Execute(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "status");
            Assert.That(tracer.ReturnValue, Is.EqualTo(eip8151Enabled ? new byte[32] : AsWord(Signer).ToBigEndian()), "output");
        }
    }

    [Test]
    public void Ecrecover_moved_by_a_state_override_keeps_the_restriction(
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction callOpcode)
    {
        CreateProcessor(parallel: false, (PrecompiledAddresses.ECRecover.Value, MovedPrecompileTarget));
        DeploySigner(ContractCode);

        Run(MeasureEcRecover(Prepare.EvmCode, 0, callOpcode, PrecompileGasLimit, ValidInput, MovedPrecompileTarget).Done);

        Measurement call = Read(0);
        ulong coldTargetAccess = GasCostOf.ColdAccountAccess - GasCostOf.WarmStateRead;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Result, Is.EqualTo(UInt256.One), "success");
            Assert.That(call.Output, Is.EqualTo(UInt256.Zero), "output");
            Assert.That(PrecompileCost(call, callOpcode), Is.EqualTo(coldTargetAccess + EcRecoverBaseCost + GasCostOf.ColdAccountAccess), "gas");
        }
    }

    [Test]
    public void Precompile_moved_to_the_ecrecover_address_is_not_restricted(
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction callOpcode)
    {
        CreateProcessor(parallel: false, (Sha256Precompile.Address, PrecompiledAddresses.ECRecover.Value));

        Run(MeasureEcRecover(Prepare.EvmCode, 0, callOpcode, PrecompileGasLimit, ValidInput).Done);

        Measurement call = Read(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Output, Is.EqualTo(new UInt256(SHA256.HashData(ValidInput), isBigEndian: true)), "output");
            Assert.That(PrecompileCost(call, callOpcode), Is.EqualTo(Sha256Precompile.Instance.BaseGasCost(_spec) + Sha256Precompile.Instance.DataGasCost(ValidInput, _spec)), "gas");
        }
    }

    private void CreateProcessor(bool parallel, (Address From, Address To)? movedPrecompile = null)
    {
        _tracedState = new TracedAccessWorldState(TestState, parallel);
        _tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumPrecompileProvider precompileProvider = new();
        PrecompileCaches precompileCaches = new(precompileProvider, new PreBlockCachesConfig(), new BlocksConfig());
        ICodeInfoRepository codeInfoRepository = new PrecompileCachedCodeInfoRepository(
            _tracedState, precompileProvider, new EthereumCodeInfoRepository(_tracedState), precompileCaches);
        if (movedPrecompile is var (from, to))
        {
            OverridableCodeInfoRepository overridable = new(codeInfoRepository, _tracedState);
            overridable.MovePrecompile(_spec, from, to);
            codeInfoRepository = overridable;
        }

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, _tracedState, Machine, codeInfoRepository, LimboLogs.Instance);
    }

    private void DeploySigner(byte[]? code)
    {
        if (code is null) return;

        TestState.CreateAccountIfNotExists(Signer, 1.Ether);
        if (code.Length > 0) TestState.InsertCode(Signer, code, _spec);
    }

    private void Run(byte[] code)
    {
        (Block block, Transaction tx) = PrepareTx(Activation, TxGasLimit, code);
        TransactionResult result = _processor.Execute(tx, new BlockExecutionContext(block.Header, _spec), NullTxTracer.Instance);
        Assert.That(result.TransactionExecuted, Is.True, "transaction executed");
    }

    private static Prepare MeasureEcRecover(Prepare code, int index, Instruction callOpcode, long gasLimit, byte[] input, Address? target = null)
    {
        code.StoreDataInMemory(0, input)
            .StoreDataInMemory(OutputOffset, Sentinel)
            .Op(Instruction.GAS)
            .PushData(32)
            .PushData(OutputOffset)
            .PushData(input.Length)
            .PushData(0);
        if (HasValueArgument(callOpcode)) code.PushData(0);
        code.PushData(target ?? PrecompiledAddresses.ECRecover.Value)
            .PushData(gasLimit)
            .Op(callOpcode)
            .Op(Instruction.GAS);

        int slot = StoreMeasurement(code, index);
        return code
            .Op(Instruction.RETURNDATASIZE).PushData(slot + 3).Op(Instruction.SSTORE)
            .PushData(OutputOffset).Op(Instruction.MLOAD).PushData(slot + 4).Op(Instruction.SSTORE);
    }

    private static Prepare MeasureBalance(Prepare code, int index, Address address)
    {
        code.Op(Instruction.GAS).PushData(address).Op(Instruction.BALANCE).Op(Instruction.GAS);
        StoreMeasurement(code, index);
        return code;
    }

    /// <summary>Stores the <c>[gas before, result, gas after]</c> stack left by a measured operation.</summary>
    private static int StoreMeasurement(Prepare code, int index)
    {
        int slot = index * SlotsPerMeasurement;
        code.PushData(slot + 2).Op(Instruction.SSTORE)
            .PushData(slot + 1).Op(Instruction.SSTORE)
            .PushData(slot).Op(Instruction.SSTORE);
        return slot;
    }

    private Measurement Read(int index)
    {
        int slot = index * SlotsPerMeasurement;
        return new Measurement(
            (ulong)(ReadSlot(slot) - ReadSlot(slot + 2)) - GasCostOf.Base,
            ReadSlot(slot + 1),
            ReadSlot(slot + 3),
            ReadSlot(slot + 4));
    }

    private UInt256 ReadSlot(int slot)
    {
        TestState.Get(new StorageCell(Recipient, (UInt256)slot), out UInt256 value);
        return value;
    }

    private static bool HasValueArgument(Instruction callOpcode) => callOpcode is Instruction.CALL or Instruction.CALLCODE;

    /// <remarks>Removes the argument pushes and the warm precompile access that the CALL itself costs.</remarks>
    private static ulong PrecompileCost(Measurement call, Instruction callOpcode) =>
        call.Cost - GasCostOf.WarmStateRead - GasCostOf.VeryLow * (HasValueArgument(callOpcode) ? 7UL : 6UL);

    private static ulong BalanceCost(Measurement balance) => balance.Cost - GasCostOf.VeryLow;

    private static UInt256 AsWord(Address address) => new(address.Bytes, isBigEndian: true);

    private static byte[] CreateInput(PrivateKey key)
    {
        ValueHash256 message = ValueKeccak.Compute("EIP-8151"u8);
        Signature signature = new Ecdsa().Sign(key, in message);
        byte[] input = new byte[128];
        message.Bytes.CopyTo(input);
        input[63] = (byte)signature.V;
        signature.Bytes.CopyTo(input.AsSpan(64));
        return input;
    }

    /// <remarks>A well-formed <c>v</c> with <c>r = s = 0</c>, which recovers no public key.</remarks>
    private static byte[] CreateUnrecoverableInput()
    {
        byte[] input = new byte[128];
        input[63] = 27;
        return input;
    }

    private readonly record struct Measurement(ulong Cost, UInt256 Result, UInt256 ReturnDataSize, UInt256 Output);
}
