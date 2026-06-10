// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Evm;

/// <summary>
/// The fork spec as a compile-time parameter: exactly the boolean flags that gate opcode
/// availability and handler-variant selection — the set
/// <see cref="EvmInstructions.GenerateOpCodes{TGasPolicy, TTracingInst}"/> reads at table-build
/// time. Gas VALUES stay runtime data on <see cref="IReleaseSpec"/> and must not be added here.
/// Implementations are empty structs whose static members are constants, so per-TSpec
/// instantiations let the JIT fold every gate and eliminate untaken cases. A spec matching no
/// struct's fingerprint runs the generic table path: custom chains and historical forks are
/// never wrong, only unspecialized. This is the complete fingerprint set — the dispatch switch
/// reads only a subset, but identity requires comparing all of it.
/// </summary>
public interface IEvmSpec
{
    static abstract bool ShiftOpcodesEnabled { get; }        // EIP-145: SHL/SHR/SAR
    static abstract bool CLZEnabled { get; }                 // EIP-7939: CLZ
    static abstract bool ReturnDataOpcodesEnabled { get; }   // EIP-211: RETURNDATASIZE/COPY
    static abstract bool ExtCodeHashOpcodeEnabled { get; }   // EIP-1052: EXTCODEHASH
    static abstract bool ChainIdOpcodeEnabled { get; }       // EIP-1344: CHAINID
    static abstract bool SelfBalanceOpcodeEnabled { get; }   // EIP-1884: SELFBALANCE
    static abstract bool BaseFeeEnabled { get; }             // EIP-3198: BASEFEE
    static abstract bool IsEip4844Enabled { get; }           // BLOBHASH + BLOBBASEFEE
    static abstract bool IsEip7843Enabled { get; }           // SLOTNUM
    static abstract bool TransientStorageEnabled { get; }    // EIP-1153: TLOAD/TSTORE
    static abstract bool MCopyIncluded { get; }              // EIP-5656: MCOPY
    static abstract bool IncludePush0Instruction { get; }    // EIP-3855: PUSH0
    static abstract bool IsEip8024Enabled { get; }           // DUPN/SWAPN/EXCHANGE
    static abstract bool DelegateCallEnabled { get; }        // EIP-7
    static abstract bool Create2OpcodeEnabled { get; }       // EIP-1014
    static abstract bool StaticCallEnabled { get; }          // EIP-214
    static abstract bool RevertOpcodeEnabled { get; }        // EIP-140
    static abstract bool UseNetGasMetering { get; }              // SSTORE variant axis 1
    static abstract bool UseNetGasMeteringWithAStipendFix { get; } // SSTORE variant axis 2
    static abstract bool IsEip8037Enabled { get; }           // SSTORE/CALL/CREATE/SELFDESTRUCT axis
    static abstract bool IsEip7708Enabled { get; }           // CALL/SELFDESTRUCT axis
}

public static class EvmSpecFingerprint
{
    // The bit layout — the ONLY place flag positions are assigned. Both packers below and the
    // guard test's FlagNames must follow this order; add new flags at the end only.
    private const int ShiftBit = 0;
    private const int ClzBit = 1;
    private const int ReturnDataBit = 2;
    private const int ExtCodeHashBit = 3;
    private const int ChainIdBit = 4;
    private const int SelfBalanceBit = 5;
    private const int BaseFeeBit = 6;
    private const int Eip4844Bit = 7;
    private const int Eip7843Bit = 8;
    private const int TransientStorageBit = 9;
    private const int MCopyBit = 10;
    private const int Push0Bit = 11;
    private const int Eip8024Bit = 12;
    private const int DelegateCallBit = 13;
    private const int Create2Bit = 14;
    private const int StaticCallBit = 15;
    private const int RevertBit = 16;
    private const int NetGasMeteringBit = 17;
    private const int NetGasStipendFixBit = 18;
    private const int Eip8037Bit = 19;
    private const int Eip7708Bit = 20;

    /// <summary>Flag names ordered by bit position, for diagnostics (see EvmSpecGuardTests).</summary>
    public static readonly string[] FlagNames =
    [
        nameof(IEvmSpec.ShiftOpcodesEnabled),
        nameof(IEvmSpec.CLZEnabled),
        nameof(IEvmSpec.ReturnDataOpcodesEnabled),
        nameof(IEvmSpec.ExtCodeHashOpcodeEnabled),
        nameof(IEvmSpec.ChainIdOpcodeEnabled),
        nameof(IEvmSpec.SelfBalanceOpcodeEnabled),
        nameof(IEvmSpec.BaseFeeEnabled),
        nameof(IEvmSpec.IsEip4844Enabled),
        nameof(IEvmSpec.IsEip7843Enabled),
        nameof(IEvmSpec.TransientStorageEnabled),
        nameof(IEvmSpec.MCopyIncluded),
        nameof(IEvmSpec.IncludePush0Instruction),
        nameof(IEvmSpec.IsEip8024Enabled),
        nameof(IEvmSpec.DelegateCallEnabled),
        nameof(IEvmSpec.Create2OpcodeEnabled),
        nameof(IEvmSpec.StaticCallEnabled),
        nameof(IEvmSpec.RevertOpcodeEnabled),
        nameof(IEvmSpec.UseNetGasMetering),
        nameof(IEvmSpec.UseNetGasMeteringWithAStipendFix),
        nameof(IEvmSpec.IsEip8037Enabled),
        nameof(IEvmSpec.IsEip7708Enabled),
    ];

    /// <summary>
    /// Packs the <see cref="IEvmSpec"/>-relevant flags of a runtime spec; the dispatcher uses
    /// this to select a specialized instantiation, or the generic table path on no match.
    /// </summary>
    public static int Compute(IReleaseSpec spec) =>
        (spec.ShiftOpcodesEnabled ? 1 << ShiftBit : 0)
        | (spec.CLZEnabled ? 1 << ClzBit : 0)
        | (spec.ReturnDataOpcodesEnabled ? 1 << ReturnDataBit : 0)
        | (spec.ExtCodeHashOpcodeEnabled ? 1 << ExtCodeHashBit : 0)
        | (spec.ChainIdOpcodeEnabled ? 1 << ChainIdBit : 0)
        | (spec.SelfBalanceOpcodeEnabled ? 1 << SelfBalanceBit : 0)
        | (spec.BaseFeeEnabled ? 1 << BaseFeeBit : 0)
        | (spec.IsEip4844Enabled ? 1 << Eip4844Bit : 0)
        | (spec.IsEip7843Enabled ? 1 << Eip7843Bit : 0)
        | (spec.TransientStorageEnabled ? 1 << TransientStorageBit : 0)
        | (spec.MCopyIncluded ? 1 << MCopyBit : 0)
        | (spec.IncludePush0Instruction ? 1 << Push0Bit : 0)
        | (spec.IsEip8024Enabled ? 1 << Eip8024Bit : 0)
        | (spec.DelegateCallEnabled ? 1 << DelegateCallBit : 0)
        | (spec.Create2OpcodeEnabled ? 1 << Create2Bit : 0)
        | (spec.StaticCallEnabled ? 1 << StaticCallBit : 0)
        | (spec.RevertOpcodeEnabled ? 1 << RevertBit : 0)
        | (spec.UseNetGasMetering ? 1 << NetGasMeteringBit : 0)
        | (spec.UseNetGasMeteringWithAStipendFix ? 1 << NetGasStipendFixBit : 0)
        | (spec.IsEip8037Enabled ? 1 << Eip8037Bit : 0)
        | (spec.IsEip7708Enabled ? 1 << Eip7708Bit : 0);

    /// <summary>Same packing for a compile-time spec, for the dispatcher and the guard test.</summary>
    public static int Compute<TSpec>() where TSpec : struct, IEvmSpec =>
        (TSpec.ShiftOpcodesEnabled ? 1 << ShiftBit : 0)
        | (TSpec.CLZEnabled ? 1 << ClzBit : 0)
        | (TSpec.ReturnDataOpcodesEnabled ? 1 << ReturnDataBit : 0)
        | (TSpec.ExtCodeHashOpcodeEnabled ? 1 << ExtCodeHashBit : 0)
        | (TSpec.ChainIdOpcodeEnabled ? 1 << ChainIdBit : 0)
        | (TSpec.SelfBalanceOpcodeEnabled ? 1 << SelfBalanceBit : 0)
        | (TSpec.BaseFeeEnabled ? 1 << BaseFeeBit : 0)
        | (TSpec.IsEip4844Enabled ? 1 << Eip4844Bit : 0)
        | (TSpec.IsEip7843Enabled ? 1 << Eip7843Bit : 0)
        | (TSpec.TransientStorageEnabled ? 1 << TransientStorageBit : 0)
        | (TSpec.MCopyIncluded ? 1 << MCopyBit : 0)
        | (TSpec.IncludePush0Instruction ? 1 << Push0Bit : 0)
        | (TSpec.IsEip8024Enabled ? 1 << Eip8024Bit : 0)
        | (TSpec.DelegateCallEnabled ? 1 << DelegateCallBit : 0)
        | (TSpec.Create2OpcodeEnabled ? 1 << Create2Bit : 0)
        | (TSpec.StaticCallEnabled ? 1 << StaticCallBit : 0)
        | (TSpec.RevertOpcodeEnabled ? 1 << RevertBit : 0)
        | (TSpec.UseNetGasMetering ? 1 << NetGasMeteringBit : 0)
        | (TSpec.UseNetGasMeteringWithAStipendFix ? 1 << NetGasStipendFixBit : 0)
        | (TSpec.IsEip8037Enabled ? 1 << Eip8037Bit : 0)
        | (TSpec.IsEip7708Enabled ? 1 << Eip7708Bit : 0);
}

/// <summary>
/// Sentinel meaning "no compile-time spec": the dispatch loop takes the generic
/// function-pointer table path. Selected for any runtime spec whose fingerprint matches no
/// specialized struct — custom chains, historical forks, plugins. Flag values are never read.
/// </summary>
public readonly struct GenericEvmSpec : IEvmSpec
{
    public static bool ShiftOpcodesEnabled => false;
    public static bool CLZEnabled => false;
    public static bool ReturnDataOpcodesEnabled => false;
    public static bool ExtCodeHashOpcodeEnabled => false;
    public static bool ChainIdOpcodeEnabled => false;
    public static bool SelfBalanceOpcodeEnabled => false;
    public static bool BaseFeeEnabled => false;
    public static bool IsEip4844Enabled => false;
    public static bool IsEip7843Enabled => false;
    public static bool TransientStorageEnabled => false;
    public static bool MCopyIncluded => false;
    public static bool IncludePush0Instruction => false;
    public static bool IsEip8024Enabled => false;
    public static bool DelegateCallEnabled => false;
    public static bool Create2OpcodeEnabled => false;
    public static bool StaticCallEnabled => false;
    public static bool RevertOpcodeEnabled => false;
    public static bool UseNetGasMetering => false;
    public static bool UseNetGasMeteringWithAStipendFix => false;
    public static bool IsEip8037Enabled => false;
    public static bool IsEip7708Enabled => false;
}

/// <summary>Cancun mainnet flags; locked to the runtime fork by EvmSpecGuardTests.</summary>
public readonly struct CancunEvmSpec : IEvmSpec
{
    public static bool ShiftOpcodesEnabled => true;
    public static bool CLZEnabled => false;
    public static bool ReturnDataOpcodesEnabled => true;
    public static bool ExtCodeHashOpcodeEnabled => true;
    public static bool ChainIdOpcodeEnabled => true;
    public static bool SelfBalanceOpcodeEnabled => true;
    public static bool BaseFeeEnabled => true;
    public static bool IsEip4844Enabled => true;
    public static bool IsEip7843Enabled => false;
    public static bool TransientStorageEnabled => true;
    public static bool MCopyIncluded => true;
    public static bool IncludePush0Instruction => true;
    public static bool IsEip8024Enabled => false;
    public static bool DelegateCallEnabled => true;
    public static bool Create2OpcodeEnabled => true;
    public static bool StaticCallEnabled => true;
    public static bool RevertOpcodeEnabled => true;
    public static bool UseNetGasMetering => true;
    public static bool UseNetGasMeteringWithAStipendFix => true;
    public static bool IsEip8037Enabled => false;
    public static bool IsEip7708Enabled => false;
}

/// <summary>Prague mainnet flags — same dispatch-relevant set as Cancun (EIP-7702 etc. do not gate opcodes).</summary>
public readonly struct PragueEvmSpec : IEvmSpec
{
    public static bool ShiftOpcodesEnabled => true;
    public static bool CLZEnabled => false;
    public static bool ReturnDataOpcodesEnabled => true;
    public static bool ExtCodeHashOpcodeEnabled => true;
    public static bool ChainIdOpcodeEnabled => true;
    public static bool SelfBalanceOpcodeEnabled => true;
    public static bool BaseFeeEnabled => true;
    public static bool IsEip4844Enabled => true;
    public static bool IsEip7843Enabled => false;
    public static bool TransientStorageEnabled => true;
    public static bool MCopyIncluded => true;
    public static bool IncludePush0Instruction => true;
    public static bool IsEip8024Enabled => false;
    public static bool DelegateCallEnabled => true;
    public static bool Create2OpcodeEnabled => true;
    public static bool StaticCallEnabled => true;
    public static bool RevertOpcodeEnabled => true;
    public static bool UseNetGasMetering => true;
    public static bool UseNetGasMeteringWithAStipendFix => true;
    public static bool IsEip8037Enabled => false;
    public static bool IsEip7708Enabled => false;
}

/// <summary>Osaka mainnet flags — Prague plus CLZ (EIP-7939).</summary>
public readonly struct OsakaEvmSpec : IEvmSpec
{
    public static bool ShiftOpcodesEnabled => true;
    public static bool CLZEnabled => true;
    public static bool ReturnDataOpcodesEnabled => true;
    public static bool ExtCodeHashOpcodeEnabled => true;
    public static bool ChainIdOpcodeEnabled => true;
    public static bool SelfBalanceOpcodeEnabled => true;
    public static bool BaseFeeEnabled => true;
    public static bool IsEip4844Enabled => true;
    public static bool IsEip7843Enabled => false;
    public static bool TransientStorageEnabled => true;
    public static bool MCopyIncluded => true;
    public static bool IncludePush0Instruction => true;
    public static bool IsEip8024Enabled => false;
    public static bool DelegateCallEnabled => true;
    public static bool Create2OpcodeEnabled => true;
    public static bool StaticCallEnabled => true;
    public static bool RevertOpcodeEnabled => true;
    public static bool UseNetGasMetering => true;
    public static bool UseNetGasMeteringWithAStipendFix => true;
    public static bool IsEip8037Enabled => false;
    public static bool IsEip7708Enabled => false;
}
