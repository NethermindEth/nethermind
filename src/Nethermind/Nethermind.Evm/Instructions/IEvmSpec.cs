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
/// never wrong, only unspecialized.
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
    /// <summary>
    /// Packs the <see cref="IEvmSpec"/>-relevant flags of a runtime spec; the dispatcher uses
    /// this to select a specialized instantiation, or the generic table path on no match.
    /// </summary>
    public static int Compute(IReleaseSpec spec) =>
        (spec.ShiftOpcodesEnabled ? 1 << 0 : 0)
        | (spec.CLZEnabled ? 1 << 1 : 0)
        | (spec.ReturnDataOpcodesEnabled ? 1 << 2 : 0)
        | (spec.ExtCodeHashOpcodeEnabled ? 1 << 3 : 0)
        | (spec.ChainIdOpcodeEnabled ? 1 << 4 : 0)
        | (spec.SelfBalanceOpcodeEnabled ? 1 << 5 : 0)
        | (spec.BaseFeeEnabled ? 1 << 6 : 0)
        | (spec.IsEip4844Enabled ? 1 << 7 : 0)
        | (spec.IsEip7843Enabled ? 1 << 8 : 0)
        | (spec.TransientStorageEnabled ? 1 << 9 : 0)
        | (spec.MCopyIncluded ? 1 << 10 : 0)
        | (spec.IncludePush0Instruction ? 1 << 11 : 0)
        | (spec.IsEip8024Enabled ? 1 << 12 : 0)
        | (spec.DelegateCallEnabled ? 1 << 13 : 0)
        | (spec.Create2OpcodeEnabled ? 1 << 14 : 0)
        | (spec.StaticCallEnabled ? 1 << 15 : 0)
        | (spec.RevertOpcodeEnabled ? 1 << 16 : 0)
        | (spec.UseNetGasMetering ? 1 << 17 : 0)
        | (spec.UseNetGasMeteringWithAStipendFix ? 1 << 18 : 0)
        | (spec.IsEip8037Enabled ? 1 << 19 : 0)
        | (spec.IsEip7708Enabled ? 1 << 20 : 0);

    /// <summary>Same packing for a compile-time spec, for the guard test and dispatcher table.</summary>
    public static int Compute<TSpec>() where TSpec : struct, IEvmSpec =>
        (TSpec.ShiftOpcodesEnabled ? 1 << 0 : 0)
        | (TSpec.CLZEnabled ? 1 << 1 : 0)
        | (TSpec.ReturnDataOpcodesEnabled ? 1 << 2 : 0)
        | (TSpec.ExtCodeHashOpcodeEnabled ? 1 << 3 : 0)
        | (TSpec.ChainIdOpcodeEnabled ? 1 << 4 : 0)
        | (TSpec.SelfBalanceOpcodeEnabled ? 1 << 5 : 0)
        | (TSpec.BaseFeeEnabled ? 1 << 6 : 0)
        | (TSpec.IsEip4844Enabled ? 1 << 7 : 0)
        | (TSpec.IsEip7843Enabled ? 1 << 8 : 0)
        | (TSpec.TransientStorageEnabled ? 1 << 9 : 0)
        | (TSpec.MCopyIncluded ? 1 << 10 : 0)
        | (TSpec.IncludePush0Instruction ? 1 << 11 : 0)
        | (TSpec.IsEip8024Enabled ? 1 << 12 : 0)
        | (TSpec.DelegateCallEnabled ? 1 << 13 : 0)
        | (TSpec.Create2OpcodeEnabled ? 1 << 14 : 0)
        | (TSpec.StaticCallEnabled ? 1 << 15 : 0)
        | (TSpec.RevertOpcodeEnabled ? 1 << 16 : 0)
        | (TSpec.UseNetGasMetering ? 1 << 17 : 0)
        | (TSpec.UseNetGasMeteringWithAStipendFix ? 1 << 18 : 0)
        | (TSpec.IsEip8037Enabled ? 1 << 19 : 0)
        | (TSpec.IsEip7708Enabled ? 1 << 20 : 0);
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
