// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8374: accessed addresses and storage keys stay warm when the frame that warmed them reverts or
/// exceptionally halts; every other effect of the failed frame is still rolled back.
/// </summary>
public class Eip8374Tests : VirtualMachineTestsBase
{
    public enum FrameOutcome
    {
        Success,
        Revert,
        Invalid,
        OutOfGas,
    }

    private const int ProbedSlot = 1;
    private const int WrittenSlot = 5;
    private const int TransientSlot = 6;
    private const int StorageCostSlot = 0x10;
    private const int AccountCostSlot = 0x11;
    private const int SecondAccountCostSlot = 0x12;
    private const int TransientResultSlot = 0x13;
    private const long SubCallGas = 300_000;
    private const ulong TxGasLimit = 2_000_000;
    // GAS, PUSH, <access>, POP, GAS around the measured access.
    private const ulong MeasureOverhead = GasCostOf.VeryLow + 2 * GasCostOf.Base;

    private static readonly Address Warmer = TestItem.AddressC;
    private static readonly Address Outer = TestItem.AddressE;
    private static readonly Address Probed = TestItem.AddressF;
    private static readonly Address Sha256Precompile = new("0x0000000000000000000000000000000000000002");

    private readonly OverridableReleaseSpec _spec = new(Bogota.Instance);
    private ISpecProvider? _specProvider;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider => _specProvider ??= new TestSpecProvider(_spec);

    private static ulong ColdStorage => Eip8038Constants.ColdStorageAccess;
    private static ulong ColdAccount => Eip8038Constants.ColdAccountAccess;
    private static ulong Warm => GasCostOf.WarmStateRead;

    [Test]
    public void Access_warmed_in_failed_sub_call_stays_warm_only_with_eip8374(
        [Values] bool eip8374, [Values] FrameOutcome outcome)
    {
        _spec.IsEip8374Enabled = eip8374;
        Assert.That(Spec.IsEip8038Enabled, Is.True, "precondition: Bogota prices accesses with EIP-8038");
        Deploy(Warmer, WarmingCode(Probed, outcome));

        byte[] code = Measure(Prepare.EvmCode.DelegateCall(Warmer, SubCallGas).Op(Instruction.POP))
            .PushData(TransientSlot).Op(Instruction.TLOAD).PushData(1).Op(Instruction.ADD)
            .PushData(TransientResultSlot).Op(Instruction.SSTORE)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, TxGasLimit, code);

        bool failed = outcome != FrameOutcome.Success;
        bool warm = !failed || eip8374;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertAccessCosts(warm);
            // EIP-8374 keeps only the warmth: the failed frame's storage and transient writes are still undone.
            Assert.That(StoredValue(WrittenSlot), Is.EqualTo(failed ? UInt256.Zero : UInt256.One));
            Assert.That(StoredValue(TransientResultSlot), Is.EqualTo(failed ? UInt256.One : (UInt256)2));
        }
    }

    [Test]
    public void Access_warmed_under_nested_reverts_stays_warm_only_with_eip8374([Values] bool eip8374, [Values] bool outerReverts)
    {
        _spec.IsEip8374Enabled = eip8374;
        Deploy(Warmer, WarmingCode(Probed, FrameOutcome.Revert));
        Prepare outer = Prepare.EvmCode.DelegateCall(Warmer, SubCallGas).Op(Instruction.POP);
        Deploy(Outer, (outerReverts ? outer.Revert(0, 0) : outer.Op(Instruction.STOP)).Done);

        TestAllTracerWithOutput result = Execute(Activation, TxGasLimit,
            Measure(Prepare.EvmCode.DelegateCall(Outer, 2 * SubCallGas).Op(Instruction.POP)).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertAccessCosts(warm: eip8374);
        }
    }

    /// <remarks>EIP-7702: a call to a delegated account also warms the delegation target, in the calling frame.</remarks>
    [Test]
    public void Delegation_target_warmed_in_reverted_sub_call_stays_warm_only_with_eip8374([Values] bool eip8374)
    {
        _spec.IsEip8374Enabled = eip8374;
        Address authority = TestItem.AddressE;
        Deploy(Probed, Prepare.EvmCode.Op(Instruction.STOP).Done);
        Deploy(authority, Bytes.Concat(Eip7702Constants.DelegationHeader, Probed.Bytes));
        Deploy(Warmer, Prepare.EvmCode.Call(authority, 50_000).Op(Instruction.POP).Revert(0, 0).Done);

        byte[] code = Prepare.EvmCode.Call(Warmer, SubCallGas).Op(Instruction.POP)
            .Op(Instruction.GAS).PushData(Probed).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.GAS)
            .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(AccountCostSlot).Op(Instruction.SSTORE)
            .Op(Instruction.GAS).PushData(authority).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.GAS)
            .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(SecondAccountCostSlot).Op(Instruction.SSTORE)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, TxGasLimit, code);

        ulong expected = (eip8374 ? Warm : ColdAccount) + MeasureOverhead;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(StoredValue(AccountCostSlot), Is.EqualTo((UInt256)expected), "delegation target");
            Assert.That(StoredValue(SecondAccountCostSlot), Is.EqualTo((UInt256)expected), "delegated account");
        }
    }

    /// <remarks>
    /// Controls that hold either way: a value-bearing CALL warms its target in the caller's frame, and
    /// precompiles are warm from the start of the transaction (EIP-2929).
    /// </remarks>
    [Test]
    public void Caller_side_and_precompile_warmth_is_independent_of_eip8374([Values] bool eip8374)
    {
        _spec.IsEip8374Enabled = eip8374;
        Deploy(Warmer, Prepare.EvmCode.PushData(Sha256Precompile).Op(Instruction.BALANCE).Op(Instruction.POP).Revert(0, 0).Done);

        byte[] code = Prepare.EvmCode.CallWithValue(Warmer, SubCallGas, UInt256.One).Op(Instruction.POP)
            .Op(Instruction.GAS).PushData(Warmer).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.GAS)
            .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(AccountCostSlot).Op(Instruction.SSTORE)
            .Op(Instruction.GAS).PushData(Sha256Precompile).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.GAS)
            .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(SecondAccountCostSlot).Op(Instruction.SSTORE)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, TxGasLimit, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(StoredValue(AccountCostSlot), Is.EqualTo((UInt256)(Warm + MeasureOverhead)), "value-call target");
            Assert.That(StoredValue(SecondAccountCostSlot), Is.EqualTo((UInt256)(Warm + MeasureOverhead)), "precompile");
            Assert.That(TestState.GetBalance(Warmer), Is.EqualTo(UInt256.Zero), "the reverted call's value transfer is undone");
        }
    }

    /// <remarks>
    /// A failing transaction still pays for its accesses at the warmth EIP-8374 gives them, while all its
    /// state is discarded.
    /// </remarks>
    [Test]
    public void Top_level_failure_discards_state_and_charges_the_eip8374_warmth()
    {
        Deploy(Warmer, WarmingCode(Probed, FrameOutcome.Revert));
        byte[] code = Measure(Prepare.EvmCode.DelegateCall(Warmer, SubCallGas).Op(Instruction.POP)).Revert(0, 0).Done;

        _spec.IsEip8374Enabled = false;
        TestAllTracerWithOutput withoutEip = Execute(Activation, TxGasLimit, code);
        _spec.IsEip8374Enabled = true;
        TestAllTracerWithOutput withEip = Execute(Activation, TxGasLimit, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutEip.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(withEip.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(withoutEip.GasSpent - withEip.GasSpent, Is.EqualTo(ColdStorage - Warm + ColdAccount - Warm));
            Assert.That(StoredValue(StorageCostSlot), Is.EqualTo(UInt256.Zero));
            Assert.That(StoredValue(WrittenSlot), Is.EqualTo(UInt256.Zero));
        }
    }

    /// <summary>Warms <see cref="ProbedSlot"/> of the caller's storage and <paramref name="address"/>, writes state, then ends as <paramref name="outcome"/>.</summary>
    private static byte[] WarmingCode(Address address, FrameOutcome outcome)
    {
        Prepare code = Prepare.EvmCode
            .PushData(ProbedSlot).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(address).Op(Instruction.BALANCE).Op(Instruction.POP)
            .PushData(1).PushData(WrittenSlot).Op(Instruction.SSTORE)
            .PushData(1).PushData(TransientSlot).Op(Instruction.TSTORE);

        return (outcome switch
        {
            FrameOutcome.Success => code.Op(Instruction.STOP),
            FrameOutcome.Revert => code.Revert(0, 0),
            FrameOutcome.Invalid => code.Op(Instruction.INVALID),
            // Expanding memory to 4 GiB cannot be paid for.
            _ => code.PushData(uint.MaxValue).Op(Instruction.MLOAD),
        }).Done;
    }

    /// <summary>Appends a cost measurement of SLOAD <see cref="ProbedSlot"/> and BALANCE <see cref="Probed"/>.</summary>
    private static Prepare Measure(Prepare code) => code
        .Op(Instruction.GAS).PushData(ProbedSlot).Op(Instruction.SLOAD).Op(Instruction.POP).Op(Instruction.GAS)
        .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(StorageCostSlot).Op(Instruction.SSTORE)
        .Op(Instruction.GAS).PushData(Probed).Op(Instruction.BALANCE).Op(Instruction.POP).Op(Instruction.GAS)
        .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(AccountCostSlot).Op(Instruction.SSTORE);

    private void AssertAccessCosts(bool warm)
    {
        Assert.That(StoredValue(StorageCostSlot), Is.EqualTo((UInt256)((warm ? Warm : ColdStorage) + MeasureOverhead)), "SLOAD");
        Assert.That(StoredValue(AccountCostSlot), Is.EqualTo((UInt256)((warm ? Warm : ColdAccount) + MeasureOverhead)), "BALANCE");
    }

    private void Deploy(Address address, byte[] code)
    {
        TestState.CreateAccount(address, 0);
        TestState.InsertCode(address, code, Spec);
    }

    private UInt256 StoredValue(int slot)
    {
        TestState.Get(new StorageCell(Recipient, (UInt256)slot), out UInt256 value);
        return value;
    }
}
