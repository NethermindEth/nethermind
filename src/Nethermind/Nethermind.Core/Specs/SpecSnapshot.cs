// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Specs;

/// <summary>
/// A self-contained readonly struct that snapshots all boolean flags from <see cref="IReleaseSpec"/>
/// into packed bitfields for fast, devirtualized access on the EVM hot path.
/// Follows the same pattern as <c>ILogger</c> in Nethermind.Logging.
/// </summary>
public readonly struct SpecSnapshot
{
    private readonly ulong _flags0; // bits 0-63
    private readonly ulong _flags1; // bits 64+
    public readonly long MaxCodeSize;
    public readonly FrozenSet<AddressAsKey> Precompiles;
    public readonly long Eip2935RingBufferSize;
    public readonly Address? Eip2935ContractAddress;
    public readonly Eip158Spec Eip158;

    // Bit positions for _flags0
    private const int TimeAdjustmentPostOlympicBit = 0;
    private const int Eip2Bit = 1;
    private const int Eip7Bit = 2;
    private const int Eip100Bit = 3;
    private const int Eip140Bit = 4;
    private const int Eip150Bit = 5;
    private const int Eip155Bit = 6;
    private const int Eip158Bit = 7;
    private const int Eip160Bit = 8;
    private const int Eip170Bit = 9;
    private const int Eip196Bit = 10;
    private const int Eip197Bit = 11;
    private const int Eip198Bit = 12;
    private const int Eip211Bit = 13;
    private const int Eip214Bit = 14;
    private const int Eip649Bit = 15;
    private const int Eip658Bit = 16;
    private const int Eip145Bit = 17;
    private const int Eip1014Bit = 18;
    private const int Eip1052Bit = 19;
    private const int Eip1283Bit = 20;
    private const int Eip1234Bit = 21;
    private const int Eip1344Bit = 22;
    private const int Eip2028Bit = 23;
    private const int Eip152Bit = 24;
    private const int Eip1108Bit = 25;
    private const int Eip1884Bit = 26;
    private const int Eip2200Bit = 27;
    private const int Eip2537Bit = 28;
    private const int Eip2565Bit = 29;
    private const int Eip2929Bit = 30;
    private const int Eip2930Bit = 31;
    private const int Eip1559Bit = 32;
    private const int Eip3198Bit = 33;
    private const int Eip3529Bit = 34;
    private const int Eip3541Bit = 35;
    private const int Eip3607Bit = 36;
    private const int Eip3651Bit = 37;
    private const int Eip1153Bit = 38;
    private const int Eip3855Bit = 39;
    private const int Eip5656Bit = 40;
    private const int Eip3860Bit = 41;
    private const int Eip4895Bit = 42;
    private const int Eip4844Bit = 43;
    private const int Eip4788Bit = 44;
    private const int Eip6110Bit = 45;
    private const int Eip7002Bit = 46;
    private const int Eip7251Bit = 47;
    private const int Eip2935Bit = 48;
    private const int Eip7709Bit = 49;
    private const int Eip6780Bit = 50;
    private const int EofBit = 51;
    private const int Eip7702Bit = 52;
    private const int Eip7823Bit = 53;
    private const int Eip7918Bit = 54;
    private const int Eip4844FeeCollectorBit = 55;
    private const int Rip7212Bit = 56;
    private const int Eip7951Bit = 57;
    private const int OpGraniteBit = 58;
    private const int OpHoloceneBit = 59;
    private const int OpJovianBit = 60;
    private const int OpIsthmusBit = 61;
    private const int Eip7623Bit = 62;
    private const int Eip7825Bit = 63;

    // Bit positions for _flags1
    private const int Eip7883Bit = 0;
    private const int Eip7934Bit = 1;
    private const int ValidateChainIdBit = 2;
    private const int ValidateReceiptsBit = 3;
    private const int Eip7594Bit = 4;
    private const int Eip7939Bit = 5;
    private const int Eip7907Bit = 6;
    private const int Rip7728Bit = 7;

    public SpecSnapshot(IReleaseSpec spec)
    {
        MaxCodeSize = spec.MaxCodeSize;
        Precompiles = spec.Precompiles;
        Eip2935RingBufferSize = spec.Eip2935RingBufferSize;
        Eip2935ContractAddress = spec.Eip2935ContractAddress;
        Eip158 = spec.Eip158;

        ulong f0 = 0;
        ulong f1 = 0;

        if (spec.IsTimeAdjustmentPostOlympic) f0 |= 1UL << TimeAdjustmentPostOlympicBit;
        if (spec.IsEip2Enabled) f0 |= 1UL << Eip2Bit;
        if (spec.IsEip7Enabled) f0 |= 1UL << Eip7Bit;
        if (spec.IsEip100Enabled) f0 |= 1UL << Eip100Bit;
        if (spec.IsEip140Enabled) f0 |= 1UL << Eip140Bit;
        if (spec.IsEip150Enabled) f0 |= 1UL << Eip150Bit;
        if (spec.IsEip155Enabled) f0 |= 1UL << Eip155Bit;
        if (spec.IsEip158Enabled) f0 |= 1UL << Eip158Bit;
        if (spec.IsEip160Enabled) f0 |= 1UL << Eip160Bit;
        if (spec.IsEip170Enabled) f0 |= 1UL << Eip170Bit;
        if (spec.IsEip196Enabled) f0 |= 1UL << Eip196Bit;
        if (spec.IsEip197Enabled) f0 |= 1UL << Eip197Bit;
        if (spec.IsEip198Enabled) f0 |= 1UL << Eip198Bit;
        if (spec.IsEip211Enabled) f0 |= 1UL << Eip211Bit;
        if (spec.IsEip214Enabled) f0 |= 1UL << Eip214Bit;
        if (spec.IsEip649Enabled) f0 |= 1UL << Eip649Bit;
        if (spec.IsEip658Enabled) f0 |= 1UL << Eip658Bit;
        if (spec.IsEip145Enabled) f0 |= 1UL << Eip145Bit;
        if (spec.IsEip1014Enabled) f0 |= 1UL << Eip1014Bit;
        if (spec.IsEip1052Enabled) f0 |= 1UL << Eip1052Bit;
        if (spec.IsEip1283Enabled) f0 |= 1UL << Eip1283Bit;
        if (spec.IsEip1234Enabled) f0 |= 1UL << Eip1234Bit;
        if (spec.IsEip1344Enabled) f0 |= 1UL << Eip1344Bit;
        if (spec.IsEip2028Enabled) f0 |= 1UL << Eip2028Bit;
        if (spec.IsEip152Enabled) f0 |= 1UL << Eip152Bit;
        if (spec.IsEip1108Enabled) f0 |= 1UL << Eip1108Bit;
        if (spec.IsEip1884Enabled) f0 |= 1UL << Eip1884Bit;
        if (spec.IsEip2200Enabled) f0 |= 1UL << Eip2200Bit;
        if (spec.IsEip2537Enabled) f0 |= 1UL << Eip2537Bit;
        if (spec.IsEip2565Enabled) f0 |= 1UL << Eip2565Bit;
        if (spec.IsEip2929Enabled) f0 |= 1UL << Eip2929Bit;
        if (spec.IsEip2930Enabled) f0 |= 1UL << Eip2930Bit;
        if (spec.IsEip1559Enabled) f0 |= 1UL << Eip1559Bit;
        if (spec.IsEip3198Enabled) f0 |= 1UL << Eip3198Bit;
        if (spec.IsEip3529Enabled) f0 |= 1UL << Eip3529Bit;
        if (spec.IsEip3541Enabled) f0 |= 1UL << Eip3541Bit;
        if (spec.IsEip3607Enabled) f0 |= 1UL << Eip3607Bit;
        if (spec.IsEip3651Enabled) f0 |= 1UL << Eip3651Bit;
        if (spec.IsEip1153Enabled) f0 |= 1UL << Eip1153Bit;
        if (spec.IsEip3855Enabled) f0 |= 1UL << Eip3855Bit;
        if (spec.IsEip5656Enabled) f0 |= 1UL << Eip5656Bit;
        if (spec.IsEip3860Enabled) f0 |= 1UL << Eip3860Bit;
        if (spec.IsEip4895Enabled) f0 |= 1UL << Eip4895Bit;
        if (spec.IsEip4844Enabled) f0 |= 1UL << Eip4844Bit;
        if (spec.IsEip4788Enabled) f0 |= 1UL << Eip4788Bit;
        if (spec.IsEip6110Enabled) f0 |= 1UL << Eip6110Bit;
        if (spec.IsEip7002Enabled) f0 |= 1UL << Eip7002Bit;
        if (spec.IsEip7251Enabled) f0 |= 1UL << Eip7251Bit;
        if (spec.IsEip2935Enabled) f0 |= 1UL << Eip2935Bit;
        if (spec.IsEip7709Enabled) f0 |= 1UL << Eip7709Bit;
        if (spec.IsEip6780Enabled) f0 |= 1UL << Eip6780Bit;
        if (spec.IsEofEnabled) f0 |= 1UL << EofBit;
        if (spec.IsEip7702Enabled) f0 |= 1UL << Eip7702Bit;
        if (spec.IsEip7823Enabled) f0 |= 1UL << Eip7823Bit;
        if (spec.IsEip7918Enabled) f0 |= 1UL << Eip7918Bit;
        if (spec.IsEip4844FeeCollectorEnabled) f0 |= 1UL << Eip4844FeeCollectorBit;
        if (spec.IsRip7212Enabled) f0 |= 1UL << Rip7212Bit;
        if (spec.IsEip7951Enabled) f0 |= 1UL << Eip7951Bit;
        if (spec.IsOpGraniteEnabled) f0 |= 1UL << OpGraniteBit;
        if (spec.IsOpHoloceneEnabled) f0 |= 1UL << OpHoloceneBit;
        if (spec.IsOpJovianEnabled) f0 |= 1UL << OpJovianBit;
        if (spec.IsOpIsthmusEnabled) f0 |= 1UL << OpIsthmusBit;
        if (spec.IsEip7623Enabled) f0 |= 1UL << Eip7623Bit;
        if (spec.IsEip7825Enabled) f0 |= 1UL << Eip7825Bit;

        if (spec.IsEip7883Enabled) f1 |= 1UL << Eip7883Bit;
        if (spec.IsEip7934Enabled) f1 |= 1UL << Eip7934Bit;
        if (spec.ValidateChainId) f1 |= 1UL << ValidateChainIdBit;
        if (spec.ValidateReceipts) f1 |= 1UL << ValidateReceiptsBit;
        if (spec.IsEip7594Enabled) f1 |= 1UL << Eip7594Bit;
        if (spec.IsEip7939Enabled) f1 |= 1UL << Eip7939Bit;
        if (spec.IsEip7907Enabled) f1 |= 1UL << Eip7907Bit;
        if (spec.IsRip7728Enabled) f1 |= 1UL << Rip7728Bit;

        _flags0 = f0;
        _flags1 = f1;
    }

    // Bool properties — bitfield reads

    public bool IsTimeAdjustmentPostOlympic => (_flags0 & (1UL << TimeAdjustmentPostOlympicBit)) != 0;
    public bool IsEip2Enabled => (_flags0 & (1UL << Eip2Bit)) != 0;
    public bool IsEip7Enabled => (_flags0 & (1UL << Eip7Bit)) != 0;
    public bool IsEip100Enabled => (_flags0 & (1UL << Eip100Bit)) != 0;
    public bool IsEip140Enabled => (_flags0 & (1UL << Eip140Bit)) != 0;
    public bool IsEip150Enabled => (_flags0 & (1UL << Eip150Bit)) != 0;
    public bool IsEip155Enabled => (_flags0 & (1UL << Eip155Bit)) != 0;
    public bool IsEip158Enabled => (_flags0 & (1UL << Eip158Bit)) != 0;
    public bool IsEip160Enabled => (_flags0 & (1UL << Eip160Bit)) != 0;
    public bool IsEip170Enabled => (_flags0 & (1UL << Eip170Bit)) != 0;
    public bool IsEip196Enabled => (_flags0 & (1UL << Eip196Bit)) != 0;
    public bool IsEip197Enabled => (_flags0 & (1UL << Eip197Bit)) != 0;
    public bool IsEip198Enabled => (_flags0 & (1UL << Eip198Bit)) != 0;
    public bool IsEip211Enabled => (_flags0 & (1UL << Eip211Bit)) != 0;
    public bool IsEip214Enabled => (_flags0 & (1UL << Eip214Bit)) != 0;
    public bool IsEip649Enabled => (_flags0 & (1UL << Eip649Bit)) != 0;
    public bool IsEip658Enabled => (_flags0 & (1UL << Eip658Bit)) != 0;
    public bool IsEip145Enabled => (_flags0 & (1UL << Eip145Bit)) != 0;
    public bool IsEip1014Enabled => (_flags0 & (1UL << Eip1014Bit)) != 0;
    public bool IsEip1052Enabled => (_flags0 & (1UL << Eip1052Bit)) != 0;
    public bool IsEip1283Enabled => (_flags0 & (1UL << Eip1283Bit)) != 0;
    public bool IsEip1234Enabled => (_flags0 & (1UL << Eip1234Bit)) != 0;
    public bool IsEip1344Enabled => (_flags0 & (1UL << Eip1344Bit)) != 0;
    public bool IsEip2028Enabled => (_flags0 & (1UL << Eip2028Bit)) != 0;
    public bool IsEip152Enabled => (_flags0 & (1UL << Eip152Bit)) != 0;
    public bool IsEip1108Enabled => (_flags0 & (1UL << Eip1108Bit)) != 0;
    public bool IsEip1884Enabled => (_flags0 & (1UL << Eip1884Bit)) != 0;
    public bool IsEip2200Enabled => (_flags0 & (1UL << Eip2200Bit)) != 0;
    public bool IsEip2537Enabled => (_flags0 & (1UL << Eip2537Bit)) != 0;
    public bool IsEip2565Enabled => (_flags0 & (1UL << Eip2565Bit)) != 0;
    public bool IsEip2929Enabled => (_flags0 & (1UL << Eip2929Bit)) != 0;
    public bool IsEip2930Enabled => (_flags0 & (1UL << Eip2930Bit)) != 0;
    public bool IsEip1559Enabled => (_flags0 & (1UL << Eip1559Bit)) != 0;
    public bool IsEip3198Enabled => (_flags0 & (1UL << Eip3198Bit)) != 0;
    public bool IsEip3529Enabled => (_flags0 & (1UL << Eip3529Bit)) != 0;
    public bool IsEip3541Enabled => (_flags0 & (1UL << Eip3541Bit)) != 0;
    public bool IsEip3607Enabled => (_flags0 & (1UL << Eip3607Bit)) != 0;
    public bool IsEip3651Enabled => (_flags0 & (1UL << Eip3651Bit)) != 0;
    public bool IsEip1153Enabled => (_flags0 & (1UL << Eip1153Bit)) != 0;
    public bool IsEip3855Enabled => (_flags0 & (1UL << Eip3855Bit)) != 0;
    public bool IsEip5656Enabled => (_flags0 & (1UL << Eip5656Bit)) != 0;
    public bool IsEip3860Enabled => (_flags0 & (1UL << Eip3860Bit)) != 0;
    public bool IsEip4895Enabled => (_flags0 & (1UL << Eip4895Bit)) != 0;
    public bool IsEip4844Enabled => (_flags0 & (1UL << Eip4844Bit)) != 0;
    public bool IsEip4788Enabled => (_flags0 & (1UL << Eip4788Bit)) != 0;
    public bool IsEip6110Enabled => (_flags0 & (1UL << Eip6110Bit)) != 0;
    public bool IsEip7002Enabled => (_flags0 & (1UL << Eip7002Bit)) != 0;
    public bool IsEip7251Enabled => (_flags0 & (1UL << Eip7251Bit)) != 0;
    public bool IsEip2935Enabled => (_flags0 & (1UL << Eip2935Bit)) != 0;
    public bool IsEip7709Enabled => (_flags0 & (1UL << Eip7709Bit)) != 0;
    public bool IsEip6780Enabled => (_flags0 & (1UL << Eip6780Bit)) != 0;
    public bool IsEofEnabled => (_flags0 & (1UL << EofBit)) != 0;
    public bool IsEip7702Enabled => (_flags0 & (1UL << Eip7702Bit)) != 0;
    public bool IsEip7823Enabled => (_flags0 & (1UL << Eip7823Bit)) != 0;
    public bool IsEip7918Enabled => (_flags0 & (1UL << Eip7918Bit)) != 0;
    public bool IsEip4844FeeCollectorEnabled => (_flags0 & (1UL << Eip4844FeeCollectorBit)) != 0;
    public bool IsRip7212Enabled => (_flags0 & (1UL << Rip7212Bit)) != 0;
    public bool IsEip7951Enabled => (_flags0 & (1UL << Eip7951Bit)) != 0;
    public bool IsOpGraniteEnabled => (_flags0 & (1UL << OpGraniteBit)) != 0;
    public bool IsOpHoloceneEnabled => (_flags0 & (1UL << OpHoloceneBit)) != 0;
    public bool IsOpJovianEnabled => (_flags0 & (1UL << OpJovianBit)) != 0;
    public bool IsOpIsthmusEnabled => (_flags0 & (1UL << OpIsthmusBit)) != 0;
    public bool IsEip7623Enabled => (_flags0 & (1UL << Eip7623Bit)) != 0;
    public bool IsEip7825Enabled => (_flags0 & (1UL << Eip7825Bit)) != 0;

    public bool IsEip7883Enabled => (_flags1 & (1UL << Eip7883Bit)) != 0;
    public bool IsEip7934Enabled => (_flags1 & (1UL << Eip7934Bit)) != 0;
    public bool ValidateChainId => (_flags1 & (1UL << ValidateChainIdBit)) != 0;
    public bool ValidateReceipts => (_flags1 & (1UL << ValidateReceiptsBit)) != 0;
    public bool IsEip7594Enabled => (_flags1 & (1UL << Eip7594Bit)) != 0;
    public bool IsEip7939Enabled => (_flags1 & (1UL << Eip7939Bit)) != 0;
    public bool IsEip7907Enabled => (_flags1 & (1UL << Eip7907Bit)) != 0;
    public bool IsRip7728Enabled => (_flags1 & (1UL << Rip7728Bit)) != 0;

    // Derived properties (from IReleaseSpecExtensions)

    public long MaxInitCodeSize => 2 * MaxCodeSize;
    public bool ClearEmptyAccountWhenTouched => IsEip158Enabled;
    public bool LimitCodeSize => IsEip170Enabled;
    public bool UseHotAndColdStorage => IsEip2929Enabled;
    public bool UseTxAccessLists => IsEip2930Enabled;
    public bool AddCoinbaseToTxAccessList => IsEip3651Enabled;
    public bool ChargeForTopLevelCreate => IsEip2Enabled;
    public bool FailOnOutOfGasCodeDeposit => IsEip2Enabled;
    public bool UseShanghaiDDosProtection => IsEip150Enabled;
    public bool UseExpDDosProtection => IsEip160Enabled;
    public bool UseLargeStateDDosProtection => IsEip1884Enabled;
    public bool ReturnDataOpcodesEnabled => IsEip211Enabled;
    public bool ChainIdOpcodeEnabled => IsEip1344Enabled;
    public bool Create2OpcodeEnabled => IsEip1014Enabled;
    public bool DelegateCallEnabled => IsEip7Enabled;
    public bool StaticCallEnabled => IsEip214Enabled;
    public bool ShiftOpcodesEnabled => IsEip145Enabled;
    public bool RevertOpcodeEnabled => IsEip140Enabled;
    public bool ExtCodeHashOpcodeEnabled => IsEip1052Enabled;
    public bool SelfBalanceOpcodeEnabled => IsEip1884Enabled;
    public bool UseConstantinopleNetGasMetering => IsEip1283Enabled;
    public bool UseIstanbulNetGasMetering => IsEip2200Enabled;
    public bool UseNetGasMetering => UseConstantinopleNetGasMetering || UseIstanbulNetGasMetering;
    public bool UseNetGasMeteringWithAStipendFix => UseIstanbulNetGasMetering;
    public bool Use63Over64Rule => UseShanghaiDDosProtection;
    public bool BaseFeeEnabled => IsEip3198Enabled;
    public bool IncludePush0Instruction => IsEip3855Enabled;
    public bool TransientStorageEnabled => IsEip1153Enabled;
    public bool WithdrawalsEnabled => IsEip4895Enabled;
    public bool SelfdestructOnlyOnSameTransaction => IsEip6780Enabled;
    public bool IsBeaconBlockRootAvailable => IsEip4788Enabled;
    public bool IsBlockHashInStateAvailable => IsEip7709Enabled;
    public bool MCopyIncluded => IsEip5656Enabled;
    public bool BlobBaseFeeEnabled => IsEip4844Enabled;
    public bool IsAuthorizationListEnabled => IsEip7702Enabled;
    public bool CLZEnabled => IsEip7939Enabled;
    public bool ModExpEnabled => IsEip198Enabled;
    public bool BN254Enabled => IsEip196Enabled && IsEip197Enabled;
    public bool BlakeEnabled => IsEip152Enabled;
    public bool Bls381Enabled => IsEip2537Enabled;
    public bool DepositsEnabled => IsEip6110Enabled;
    public bool WithdrawalRequestsEnabled => IsEip7002Enabled;
    public bool ConsolidationRequestsEnabled => IsEip7251Enabled;
    public bool RequestsEnabled => ConsolidationRequestsEnabled || WithdrawalRequestsEnabled || DepositsEnabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPrecompile(Address address) => Precompiles.Contains(address);
}
