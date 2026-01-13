// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs;

public class ReleaseSpec : IReleaseSpec
{
    // Compiling of IReleaseSpec is very slow when these members are Default
    // Interface Members, therefore, we reintroduce them as concrete members.

    public string Name { get; set; } = "Custom";
    public long MaximumExtraDataSize { get; set; }
    public long MaxCodeSize { get; set; }
    public long MinGasLimit { get; set; }
    public long MinHistoryRetentionEpochs { get; set; }
    public long GasLimitBoundDivisor { get; set; }
    public UInt256 BlockReward { get; set; }
    public long DifficultyBombDelay { get; set; }
    public long DifficultyBoundDivisor { get; set; }
    public long? FixedDifficulty { get; set; }
    public int MaximumUncleCount { get; set; }
    public bool IsTimeAdjustmentPostOlympic { get; set; }
    public bool IsEip2Enabled { get; set; }
    public bool IsEip7Enabled { get; set; }
    public bool IsEip100Enabled { get; set; }
    public bool IsEip140Enabled { get; set; }
    public bool IsEip150Enabled { get; set; }
    public bool IsEip155Enabled { get; set; }
    public bool IsEip158Enabled { get; set; }
    public bool IsEip160Enabled { get; set; }
    public bool IsEip170Enabled { get; set; }
    public bool IsEip196Enabled { get; set; }
    public bool IsEip197Enabled { get; set; }
    public bool IsEip198Enabled { get; set; }
    public bool IsEip211Enabled { get; set; }
    public bool IsEip214Enabled { get; set; }
    public bool IsEip649Enabled { get; set; }
    public bool IsEip658Enabled { get; set; }
    public bool IsEip145Enabled { get; set; }
    public bool IsEip1014Enabled { get; set; }
    public bool IsEip1052Enabled { get; set; }
    public bool IsEip1283Enabled { get; set; }
    public bool IsEip1234Enabled { get; set; }
    public bool IsEip1344Enabled { get; set; }
    public bool IsEip2028Enabled { get; set; }
    public bool IsEip152Enabled { get; set; }
    public bool IsEip1108Enabled { get; set; }
    public bool IsEip1884Enabled { get; set; }
    public bool IsEip2200Enabled { get; set; }
    public bool IsEip2537Enabled { get; set; }
    public bool IsEip2565Enabled { get; set; }
    public bool IsEip2929Enabled { get; set; }
    public bool IsEip2930Enabled { get; set; }

    public long MaxInitCodeSize => 2 * MaxCodeSize;

    public bool IsEip158IgnoredAccount(Address address) => false;

    public bool DepositsEnabled => IsEip6110Enabled;
    public bool WithdrawalRequestsEnabled => IsEip7002Enabled;
    public bool ConsolidationRequestsEnabled => IsEip7251Enabled;

    // STATE related
    public bool ClearEmptyAccountWhenTouched => IsEip158Enabled;

    // VM
    public bool LimitCodeSize => IsEip170Enabled;
    public bool UseHotAndColdStorage => IsEip2929Enabled;
    public bool UseTxAccessLists => IsEip2930Enabled;
    public bool AddCoinbaseToTxAccessList => IsEip3651Enabled;

    public bool ModExpEnabled => IsEip198Enabled;
    public bool BN254Enabled => IsEip196Enabled && IsEip197Enabled;
    public bool BlakeEnabled => IsEip152Enabled;
    public bool Bls381Enabled => IsEip2537Enabled;

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
    public bool UseNetGasMetering => UseConstantinopleNetGasMetering | UseIstanbulNetGasMetering;
    public bool UseNetGasMeteringWithAStipendFix => UseIstanbulNetGasMetering;
    public bool Use63Over64Rule => UseShanghaiDDosProtection;

    public bool BaseFeeEnabled => IsEip3198Enabled;

    // EVM Related
    public bool IncludePush0Instruction => IsEip3855Enabled;
    public bool TransientStorageEnabled => IsEip1153Enabled;

    public bool WithdrawalsEnabled => IsEip4895Enabled;
    public bool SelfdestructOnlyOnSameTransaction => IsEip6780Enabled;

    public bool IsBeaconBlockRootAvailable => IsEip4788Enabled;
    public bool IsBlockHashInStateAvailable => IsEip7709Enabled;
    public bool MCopyIncluded => IsEip5656Enabled;

    public bool BlobBaseFeeEnabled => IsEip4844Enabled;

    bool IReleaseSpec.IsAuthorizationListEnabled => IsEip7702Enabled;

    public bool RequestsEnabled => ConsolidationRequestsEnabled || WithdrawalRequestsEnabled || DepositsEnabled;

    public ProofVersion BlobProofVersion => IsEip7594Enabled ? ProofVersion.V1 : ProofVersion.V0;

    public bool CLZEnabled => IsEip7939Enabled;

    public bool IsPrecompile(Address address) => ((IReleaseSpec)this).Precompiles.Contains(address);

    // used only in testing
    public ReleaseSpec Clone() => (ReleaseSpec)MemberwiseClone();

    public bool IsEip1559Enabled
    {
        get => _isEip1559Enabled || IsEip4844Enabled;
        set => _isEip1559Enabled = value;
    }

    public bool IsEip3198Enabled { get; set; }
    public bool IsEip3529Enabled { get; set; }
    public bool IsEip3607Enabled { get; set; }
    public bool IsEip3541Enabled { get; set; }
    public bool ValidateChainId { get; set; } = true;
    public bool ValidateReceipts { get; set; }
    public long Eip1559TransitionBlock { get; set; }
    public ulong WithdrawalTimestamp { get; set; }
    public ulong Eip4844TransitionTimestamp { get; set; }
    public Address FeeCollector { get; set; }
    public UInt256? Eip1559BaseFeeMinValue { get; set; }
    public UInt256 ForkBaseFee { get; set; } = Eip1559Constants.DefaultForkBaseFee;
    public UInt256 BaseFeeMaxChangeDenominator { get; set; } = Eip1559Constants.DefaultBaseFeeMaxChangeDenominator;
    public long ElasticityMultiplier { get; set; } = Eip1559Constants.DefaultElasticityMultiplier;
    public IBaseFeeCalculator BaseFeeCalculator { get; set; } = new DefaultBaseFeeCalculator();
    public bool IsEip1153Enabled { get; set; }
    public bool IsEip3651Enabled { get; set; }
    public bool IsEip3855Enabled { get; set; }
    public bool IsEip3860Enabled { get; set; }
    public bool IsEip4895Enabled { get; set; }
    public bool IsEip4844Enabled { get; set; }
    public bool IsEip7951Enabled { get; set; }
    public bool IsRip7212Enabled { get; set; }
    public bool IsOpGraniteEnabled { get; set; }
    public bool IsOpHoloceneEnabled { get; set; }
    public bool IsOpIsthmusEnabled { get; set; }
    public bool IsOpJovianEnabled { get; set; }
    public bool IsEip7623Enabled { get; set; }
    public bool IsEip7883Enabled { get; set; }
    public bool IsEip5656Enabled { get; set; }
    public bool IsEip6780Enabled { get; set; }
    public bool IsEip4788Enabled { get; set; }
    public bool IsEip7702Enabled { get; set; }
    public bool IsEip7823Enabled { get; set; }
    public bool IsEip4844FeeCollectorEnabled { get; set; }
    public bool IsEip7002Enabled { get; set; }
    public bool IsEip7251Enabled { get; set; }
    public bool IsEip7825Enabled { get; set; }
    public bool IsEip7918Enabled { get; set; }
    public bool IsEip7934Enabled { get; set; }
    public int Eip7934MaxRlpBlockSize { get; set; }
    public bool IsEip7907Enabled { get; set; }

    public ulong TargetBlobCount { get; set; }
    public ulong MaxBlobCount { get; set; }

    public ulong MaxBlobsPerTx =>
        IsEip7594Enabled ? Math.Min(Eip7594Constants.MaxBlobsPerTx, MaxBlobCount) : MaxBlobCount;

    public UInt256 BlobBaseFeeUpdateFraction { get; set; }


    private Address _eip7251ContractAddress;

    public Address Eip7251ContractAddress
    {
        get => IsEip7251Enabled ? _eip7251ContractAddress : null;
        set => _eip7251ContractAddress = value;
    }

    private Address _eip7002ContractAddress;

    public Address Eip7002ContractAddress
    {
        get => IsEip7002Enabled ? _eip7002ContractAddress : null;
        set => _eip7002ContractAddress = value;
    }

    private Address _eip4788ContractAddress;

    public Address Eip4788ContractAddress
    {
        get => IsEip4788Enabled ? _eip4788ContractAddress : null;
        set => _eip4788ContractAddress = value;
    }

    public bool IsEofEnabled { get; set; }

    public bool IsEip6110Enabled { get; set; }

    private Address _depositContractAddress;

    public Address DepositContractAddress
    {
        get => IsEip6110Enabled ? _depositContractAddress : null;
        set => _depositContractAddress = value;
    }

    public bool IsEip2935Enabled { get; set; }
    public bool IsEip7709Enabled { get; set; }

    private Address _eip2935ContractAddress;
    private bool _isEip1559Enabled;

    public Address Eip2935ContractAddress
    {
        get => IsEip2935Enabled ? _eip2935ContractAddress : null;
        set => _eip2935ContractAddress = value;
    }

    public bool IsEip7594Enabled { get; set; }

    Array? IReleaseSpec.EvmInstructionsNoTrace { get; set; }

    Array? IReleaseSpec.EvmInstructionsTraced { get; set; }
    public bool IsEip7939Enabled { get; set; }
    public bool IsRip7728Enabled { get; set; }

    private FrozenSet<AddressAsKey>? _precompiles;
    FrozenSet<AddressAsKey> IReleaseSpec.Precompiles => _precompiles ??= BuildPrecompilesCache();
    public long Eip2935RingBufferSize { get; set; } = Eip2935Constants.RingBufferSize;

    public virtual FrozenSet<AddressAsKey> BuildPrecompilesCache()
    {
        HashSet<AddressAsKey> cache = new();

        cache.Add(PrecompiledAddresses.EcRecover);
        cache.Add(PrecompiledAddresses.Sha256);
        cache.Add(PrecompiledAddresses.Ripemd160);
        cache.Add(PrecompiledAddresses.Identity);

        if (IsEip198Enabled) cache.Add(PrecompiledAddresses.ModExp);
        if (IsEip196Enabled && IsEip197Enabled)
        {
            cache.Add(PrecompiledAddresses.Bn128Add);
            cache.Add(PrecompiledAddresses.Bn128Mul);
            cache.Add(PrecompiledAddresses.Bn128Pairing);
        }

        if (IsEip152Enabled) cache.Add(PrecompiledAddresses.Blake2F);
        if (IsEip4844Enabled) cache.Add(PrecompiledAddresses.PointEvaluation);
        if (IsEip2537Enabled)
        {
            cache.Add(PrecompiledAddresses.Bls12G1Add);
            cache.Add(PrecompiledAddresses.Bls12G1Msm);
            cache.Add(PrecompiledAddresses.Bls12G2Add);
            cache.Add(PrecompiledAddresses.Bls12G2Msm);
            cache.Add(PrecompiledAddresses.Bls12PairingCheck);
            cache.Add(PrecompiledAddresses.Bls12MapFpToG1);
            cache.Add(PrecompiledAddresses.Bls12MapFp2ToG2);
        }

        if (IsRip7212Enabled || IsEip7951Enabled) cache.Add(PrecompiledAddresses.P256Verify);
        if (IsRip7728Enabled) cache.Add(PrecompiledAddresses.L1Sload);

        return cache.ToFrozenSet();
    }
}
