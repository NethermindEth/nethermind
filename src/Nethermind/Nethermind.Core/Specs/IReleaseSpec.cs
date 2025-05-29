// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IReleaseSpec : IEip1559Spec, IReceiptSpec
    {
        public string Name { get; }
        long MaximumExtraDataSize { get; }
        long MaxCodeSize { get; }
        //EIP-3860: Limit and meter initcode
        long MaxInitCodeSize => 2 * MaxCodeSize;
        long MinGasLimit { get; }
        long GasLimitBoundDivisor { get; }
        UInt256 BlockReward { get; }
        long DifficultyBombDelay { get; }
        long DifficultyBoundDivisor { get; }
        long? FixedDifficulty { get; }
        int MaximumUncleCount { get; }

        /// <summary>
        /// ---
        /// In chainspec - Ethash.Duration
        /// </summary>
        bool IsTimeAdjustmentPostOlympic { get; }

        /// <summary>
        /// Homestead contract creation via transaction cost set to 21000 + 32000 (previously 21000)
        /// Failing init does not create an empty code contract
        /// Difficulty adjustment changed
        /// Transaction signature uniqueness (s-value has to be less or equal than than secp256k1n/2)
        /// </summary>
        bool IsEip2Enabled { get; }

        /// <summary>
        /// Homestead DELEGATECALL instruction added
        /// </summary>
        bool IsEip7Enabled { get; }

        /// <summary>
        /// Byzantium Change difficulty adjustment to target mean block time including uncles
        /// </summary>
        bool IsEip100Enabled { get; }

        /// <summary>
        /// Byzantium REVERT instruction in the Ethereum Virtual Machine
        /// ---
        /// in chainspec Ethash.Eip100bTransition
        /// </summary>
        bool IsEip140Enabled { get; }

        /// <summary>
        /// Tangerine Whistle Gas cost of IO operations increased
        /// </summary>
        bool IsEip150Enabled { get; }

        /// <summary>
        /// Spurious Dragon Chain ID in signatures (replay attack protection)
        /// </summary>
        bool IsEip155Enabled { get; }

        /// <summary>
        /// Spurious Dragon State clearing
        /// </summary>
        bool IsEip158Enabled { get; }

        /// <summary>
        /// Spurious Dragon EXP cost increase
        /// </summary>
        bool IsEip160Enabled { get; }

        /// <summary>
        /// Spurious Dragon Code size limit
        /// ---
        /// in chainspec MaxCodeSizeTransition
        /// </summary>
        bool IsEip170Enabled { get; }

        /// <summary>
        /// Byzantium Precompiled contracts for addition and scalar multiplication on the elliptic curve alt_bn128
        /// ---
        /// in chainspec in builtin accounts
        /// </summary>
        bool IsEip196Enabled { get; }

        /// <summary>
        /// Byzantium Precompiled contracts for optimal ate pairing check on the elliptic curve alt_bn128
        /// ---
        /// in chainspec in builtin accounts
        /// </summary>
        bool IsEip197Enabled { get; }

        /// <summary>
        /// Byzantium Precompiled contract for bigint modular exponentiation
        /// ---
        /// in chainspec in builtin accounts
        /// </summary>
        bool IsEip198Enabled { get; }

        /// <summary>
        /// Byzantium New opcodes: RETURNDATASIZE and RETURNDATACOPY
        /// </summary>
        bool IsEip211Enabled { get; }

        /// <summary>
        /// Byzantium New opcode STATICCALL
        /// </summary>
        bool IsEip214Enabled { get; }

        /// <summary>
        /// Byzantium Difficulty Bomb Delay and Block Reward Reduction
        /// ---
        /// in chainspec as DifficultyBombDelays
        /// </summary>
        bool IsEip649Enabled { get; }

        /// <summary>
        /// Constantinople SHL, SHR, SAR instructions
        /// </summary>
        bool IsEip145Enabled { get; }

        /// <summary>
        /// Constantinople Skinny CREATE2
        /// </summary>
        bool IsEip1014Enabled { get; }

        /// <summary>
        /// Constantinople EXTCODEHASH instructions
        /// </summary>
        bool IsEip1052Enabled { get; }

        /// <summary>
        /// Constantinople Net gas metering for SSTORE operations
        /// </summary>
        bool IsEip1283Enabled { get; }

        /// <summary>
        /// Constantinople Difficulty Bomb Delay and Block Reward Adjustment
        /// ---
        /// in chainspec as DifficultyBombDelays and BlockReward
        /// </summary>
        bool IsEip1234Enabled { get; }

        /// <summary>
        /// Istanbul ChainID opcode
        /// </summary>
        bool IsEip1344Enabled { get; }

        /// <summary>
        /// Istanbul transaction data gas cost reduction
        /// </summary>
        bool IsEip2028Enabled { get; }

        /// <summary>
        /// Istanbul Blake2F precompile
        /// </summary>
        bool IsEip152Enabled { get; }

        /// <summary>
        /// Istanbul alt_bn128 gas cost reduction
        /// </summary>
        bool IsEip1108Enabled { get; }

        /// <summary>
        /// Istanbul state opcodes gas cost increase
        /// </summary>
        bool IsEip1884Enabled { get; }

        /// <summary>
        /// Istanbul net-metered SSTORE
        /// </summary>
        bool IsEip2200Enabled { get; }

        /// <summary>
        /// Berlin BLS crypto precompiles
        /// </summary>
        bool IsEip2537Enabled { get; }

        /// <summary>
        /// Berlin MODEXP precompiles
        /// </summary>
        bool IsEip2565Enabled { get; }

        /// <summary>
        /// Berlin gas cost increases for state reading opcodes
        /// </summary>
        bool IsEip2929Enabled { get; }

        /// <summary>
        /// Berlin access lists
        /// </summary>
        bool IsEip2930Enabled { get; }

        /// <summary>
        /// Should EIP158 be ignored for this account.
        /// </summary>
        /// <remarks>THis is needed for SystemUser account compatibility with Parity.</remarks>
        /// <param name="address"></param>
        /// <returns></returns>
        bool IsEip158IgnoredAccount(Address address) => false;

        /// <summary>
        /// BaseFee opcode
        /// </summary>
        bool IsEip3198Enabled { get; }

        /// <summary>
        /// Reduction in refunds
        /// </summary>
        bool IsEip3529Enabled { get; }

        /// <summary>
        /// Reject new contracts starting with the 0xEF byte
        /// </summary>
        bool IsEip3541Enabled { get; }

        /// <summary>
        /// Reject transactions where senders have non-empty code hash
        /// </summary>
        bool IsEip3607Enabled { get; }

        /// <summary>
        /// Warm COINBASE
        /// </summary>
        bool IsEip3651Enabled { get; }

        /// <summary>
        /// Transient storage
        /// </summary>
        bool IsEip1153Enabled { get; }


        /// <summary>
        /// PUSH0 instruction
        /// </summary>
        bool IsEip3855Enabled { get; }

        /// <summary>
        /// MCOPY instruction
        /// </summary>
        bool IsEip5656Enabled { get; }

        /// <summary>
        /// EIP-3860: Limit and meter initcode
        /// </summary>
        bool IsEip3860Enabled { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the
        /// <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>
        /// validator withdrawals are enabled.
        /// </summary>
        bool IsEip4895Enabled { get; }

        /// <summary>
        /// Blob transactions
        /// </summary>
        bool IsEip4844Enabled { get; }

        /// <summary>
        /// Parent Beacon Block precompile
        /// </summary>
        bool IsEip4788Enabled { get; }
        Address? Eip4788ContractAddress { get; }


        /// <summary>
        /// EIP-6110: Supply validator deposits on chain
        /// </summary>
        bool IsEip6110Enabled { get; }
        bool DepositsEnabled => IsEip6110Enabled;
        Address DepositContractAddress { get; }

        /// <summary>
        /// Execution layer triggerable exits
        /// </summary>
        bool IsEip7002Enabled { get; }
        bool WithdrawalRequestsEnabled => IsEip7002Enabled;
        Address Eip7002ContractAddress { get; }


        /// <summary>
        /// EIP-7251: triggered consolidations
        /// </summary>
        bool IsEip7251Enabled { get; }
        bool ConsolidationRequestsEnabled => IsEip7251Enabled;
        Address Eip7251ContractAddress { get; }


        /// <summary>
        /// Save historical block hashes in state
        /// </summary>
        bool IsEip2935Enabled { get; }

        /// <summary>
        /// Fetch blockHashes from the state for BLOCKHASH opCode
        /// </summary>
        bool IsEip7709Enabled { get; }
        Address Eip2935ContractAddress { get; }

        /// <summary>
        /// SELFDESTRUCT only in same transaction
        /// </summary>
        bool IsEip6780Enabled { get; }

        /// <summary>
        /// Eof execution env in EVM
        /// </summary>
        bool IsEofEnabled { get; }

        /// <summary>
        /// Transactions that allows code delegation for EOA
        /// </summary>
        bool IsEip7702Enabled { get; }

        /// <summary>
        /// Set upper bounds for MODEXP
        /// </summary>
        bool IsEip7823Enabled { get; }

        /// <summary>
        /// Blob base fee bounded by execution cost
        /// </summary>
        bool IsEip7918Enabled { get; }

        /// <summary>
        /// Blob base fee collection for Gnosis
        /// </summary>
        bool IsEip4844FeeCollectorEnabled { get; }

        /// <summary>
        /// Secp256r1 precompile
        /// </summary>
        bool IsRip7212Enabled { get; }

        /// OP Granite
        bool IsOpGraniteEnabled { get; }

        /// OP Holocene
        bool IsOpHoloceneEnabled { get; }

        // OP Isthmus
        bool IsOpIsthmusEnabled { get; }

        /// <summary>
        ///  Increase call data cost
        /// </summary>
        bool IsEip7623Enabled { get; }

        /// <summary>
        ///  Transaction gas limit cap
        /// </summary>
        bool IsEip7825Enabled { get; }

        /// <summary>
        ///  Increase ModExp Gas Cost
        /// </summary>
        bool IsEip7883Enabled { get; }

        /// <summary>
        /// Should transactions be validated against chainId.
        /// </summary>
        /// <remarks>Backward compatibility for early Kovan blocks.</remarks>
        bool ValidateChainId => true;

        /// <summary>
        /// EIP-7780: Add blob schedule to EL config files
        /// </summary>
        public ulong TargetBlobCount { get; }
        public ulong MaxBlobCount { get; }
        public UInt256 BlobBaseFeeUpdateFraction { get; }

        public ulong WithdrawalTimestamp { get; }

        public ulong Eip4844TransitionTimestamp { get; }

        // STATE related
        public bool ClearEmptyAccountWhenTouched => IsEip158Enabled;

        // VM
        public bool LimitCodeSize => IsEip170Enabled;

        public bool UseHotAndColdStorage => IsEip2929Enabled;

        public bool UseTxAccessLists => IsEip2930Enabled;

        public bool AddCoinbaseToTxAccessList => IsEip3651Enabled;

        public bool ModExpEnabled => IsEip198Enabled;

        public bool Bn128Enabled => IsEip196Enabled && IsEip197Enabled;

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

        bool IsAuthorizationListEnabled => IsEip7702Enabled;

        public bool RequestsEnabled => ConsolidationRequestsEnabled || WithdrawalRequestsEnabled || DepositsEnabled;

        public bool IsEip7594Enabled { get; }

        /// <summary>
        /// This property holds an array that, at runtime, is actually an array of function pointers
        /// with the signature:
        /// <c>delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType></c>.
        /// The array is lazily populated with JIT-optimized instructions for an EVM without tracing,
        /// but it cannot be explicitly typed as such due to cross-project layering constraints.
        /// </summary>
        /// <remarks>
        /// Because of these layering issues, the property is declared as <see cref="System.Array"/>
        /// even though it internally represents a typed array of function pointers.
        /// </remarks>
        public Array? EvmInstructionsNoTrace { get; set; }

        /// <summary>
        /// This property holds an array that, at runtime, is actually an array of function pointers
        /// with the signature:
        /// <c>delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType></c>.
        /// The array is lazily populated with JIT-optimized instructions for an EVM,
        /// capturing additional tracing data. It cannot be explicitly typed as such due to cross-project
        /// layering constraints.
        /// </summary>
        /// <remarks>
        /// Because of these layering issues, the property is declared as <see cref="System.Array"/>
        /// even though it internally represents a typed array of function pointers.
        /// </remarks>
        public Array? EvmInstructionsTraced { get; set; }

        public ProofVersion BlobProofVersion => IsEip7594Enabled ? ProofVersion.V1 : ProofVersion.V0;
    }
}
