// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>The consensus-specs v1.7.0-beta.2 mainnet presets of phase0 to gloas and <c>configs/mainnet.yaml</c>, verbatim, one section per file.</summary>
internal static class PinnedMainnetSpec
{
    public const string Yaml = """
#### preset phase0
# Mainnet preset - Phase0

# Misc
# ---------------------------------------------------------------
# 2**6 (= 64) committees
MAX_COMMITTEES_PER_SLOT: 64
# 2**7 (= 128) committees
TARGET_COMMITTEE_SIZE: 128
# 2**11 (= 2,048) validators
MAX_VALIDATORS_PER_COMMITTEE: 2048
# See issue 563
SHUFFLE_ROUND_COUNT: 90
# 4
HYSTERESIS_QUOTIENT: 4
# 1 (minus 0.25)
HYSTERESIS_DOWNWARD_MULTIPLIER: 1
# 5 (plus 1.25)
HYSTERESIS_UPWARD_MULTIPLIER: 5

# Gwei values
# ---------------------------------------------------------------
# 2**0 * 10**9 (= 1,000,000,000) Gwei
MIN_DEPOSIT_AMOUNT: 1000000000
# 2**5 * 10**9 (= 32,000,000,000) Gwei
MAX_EFFECTIVE_BALANCE: 32000000000
# 2**0 * 10**9 (= 1,000,000,000) Gwei
EFFECTIVE_BALANCE_INCREMENT: 1000000000

# Time parameters
# ---------------------------------------------------------------
# 2**0 (= 1) slots
MIN_ATTESTATION_INCLUSION_DELAY: 1
# 2**5 (= 32) slots
SLOTS_PER_EPOCH: 32
# 2**0 (= 1) epochs
MIN_SEED_LOOKAHEAD: 1
# 2**2 (= 4) epochs
MAX_SEED_LOOKAHEAD: 4
# 2**6 (= 64) epochs
EPOCHS_PER_ETH1_VOTING_PERIOD: 64
# 2**13 (= 8,192) slots
SLOTS_PER_HISTORICAL_ROOT: 8192
# 2**2 (= 4) epochs
MIN_EPOCHS_TO_INACTIVITY_PENALTY: 4

# State list lengths
# ---------------------------------------------------------------
# 2**16 (= 65,536) epochs
EPOCHS_PER_HISTORICAL_VECTOR: 65536
# 2**13 (= 8,192) epochs
EPOCHS_PER_SLASHINGS_VECTOR: 8192
# 2**24 (= 16,777,216) historical roots
HISTORICAL_ROOTS_LIMIT: 16777216
# 2**40 (= 1,099,511,627,776) validator spots
VALIDATOR_REGISTRY_LIMIT: 1099511627776

# Rewards and penalties
# ---------------------------------------------------------------
# 2**6 (= 64)
BASE_REWARD_FACTOR: 64
# 2**9 (= 512)
WHISTLEBLOWER_REWARD_QUOTIENT: 512
# 2**3 (= 8)
PROPOSER_REWARD_QUOTIENT: 8
# 2**26 (= 67,108,864)
INACTIVITY_PENALTY_QUOTIENT: 67108864
# 2**7 (= 128) (lower safety margin at Phase0 genesis)
MIN_SLASHING_PENALTY_QUOTIENT: 128
# 1 (lower safety margin at Phase0 genesis)
PROPORTIONAL_SLASHING_MULTIPLIER: 1

# Max operations per block
# ---------------------------------------------------------------
# 2**4 (= 16) proposer slashings
MAX_PROPOSER_SLASHINGS: 16
# 2**1 (= 2) attester slashings
MAX_ATTESTER_SLASHINGS: 2
# 2**7 (= 128) attestations
MAX_ATTESTATIONS: 128
# 2**4 (= 16) deposits
MAX_DEPOSITS: 16
# 2**4 (= 16) voluntary exits
MAX_VOLUNTARY_EXITS: 16
#### preset altair
# Mainnet preset - Altair

# Rewards and penalties
# ---------------------------------------------------------------
# 3 * 2**24 (= 50,331,648)
INACTIVITY_PENALTY_QUOTIENT_ALTAIR: 50331648
# 2**6 (= 64)
MIN_SLASHING_PENALTY_QUOTIENT_ALTAIR: 64
# 2
PROPORTIONAL_SLASHING_MULTIPLIER_ALTAIR: 2

# Sync committee
# ---------------------------------------------------------------
# 2**9 (= 512) participants
SYNC_COMMITTEE_SIZE: 512
# 2**8 (= 256) epochs
EPOCHS_PER_SYNC_COMMITTEE_PERIOD: 256

# Sync protocol
# ---------------------------------------------------------------
# 2**0 (= 1) participants
MIN_SYNC_COMMITTEE_PARTICIPANTS: 1
# SLOTS_PER_EPOCH * EPOCHS_PER_SYNC_COMMITTEE_PERIOD (= 32 * 256) epochs
UPDATE_TIMEOUT: 8192
#### preset bellatrix
# Mainnet preset - Bellatrix

# Rewards and penalties
# ---------------------------------------------------------------
# 2**24 (= 16,777,216)
INACTIVITY_PENALTY_QUOTIENT_BELLATRIX: 16777216
# 2**5 (= 32)
MIN_SLASHING_PENALTY_QUOTIENT_BELLATRIX: 32
# 3
PROPORTIONAL_SLASHING_MULTIPLIER_BELLATRIX: 3

# Execution
# ---------------------------------------------------------------
# 2**30 (= 1,073,741,824) bytes
MAX_BYTES_PER_TRANSACTION: 1073741824
# 2**20 (= 1,048,576) transactions
MAX_TRANSACTIONS_PER_PAYLOAD: 1048576
# 2**8 (= 256) bytes
BYTES_PER_LOGS_BLOOM: 256
# 2**5 (= 32) bytes
MAX_EXTRA_DATA_BYTES: 32
#### preset capella
# Mainnet preset - Capella

# Max operations per block
# ---------------------------------------------------------------
# 2**4 (= 16) credential changes
MAX_BLS_TO_EXECUTION_CHANGES: 16

# Execution
# ---------------------------------------------------------------
# 2**4 (= 16) withdrawals
MAX_WITHDRAWALS_PER_PAYLOAD: 16

# Withdrawals processing
# ---------------------------------------------------------------
# 2**14 (= 16,384) validators
MAX_VALIDATORS_PER_WITHDRAWALS_SWEEP: 16384
#### preset deneb
# Mainnet preset - Deneb

# Execution
# ---------------------------------------------------------------
# 2**12 (= 4,096) commitments
MAX_BLOB_COMMITMENTS_PER_BLOCK: 4096

# Networking
# ---------------------------------------------------------------
# floorlog2(get_generalized_index(BeaconBlockBody, 'blob_kzg_commitments')) + 1 + ceillog2(MAX_BLOB_COMMITMENTS_PER_BLOCK) (= 4 + 1 + 12 = 17)
KZG_COMMITMENT_INCLUSION_PROOF_DEPTH: 17

# Blob
# ---------------------------------------------------------------
# 2**12 (= 4,096) field elements
FIELD_ELEMENTS_PER_BLOB: 4096
#### preset electra
# Mainnet preset - Electra

# Gwei values
# ---------------------------------------------------------------
# 2**5 * 10**9 (= 32,000,000,000) Gwei
MIN_ACTIVATION_BALANCE: 32000000000
# 2**11 * 10**9 (= 2,048,000,000,000) Gwei
MAX_EFFECTIVE_BALANCE_ELECTRA: 2048000000000

# Rewards and penalties
# ---------------------------------------------------------------
# 2**12 (= 4,096)
MIN_SLASHING_PENALTY_QUOTIENT_ELECTRA: 4096
# 2**12 (= 4,096)
WHISTLEBLOWER_REWARD_QUOTIENT_ELECTRA: 4096

# State list lengths
# ---------------------------------------------------------------
# 2**27 (= 134,217,728) pending deposits
PENDING_DEPOSITS_LIMIT: 134217728
# 2**27 (= 134,217,728) pending partial withdrawals
PENDING_PARTIAL_WITHDRAWALS_LIMIT: 134217728
# 2**18 (= 262,144) pending consolidations
PENDING_CONSOLIDATIONS_LIMIT: 262144

# Max operations per block
# ---------------------------------------------------------------
# 2**0 (= 1) attester slashings
MAX_ATTESTER_SLASHINGS_ELECTRA: 1
# 2**3 (= 8) attestations
MAX_ATTESTATIONS_ELECTRA: 8

# Execution
# ---------------------------------------------------------------
# 2**13 (= 8,192) deposit requests
MAX_DEPOSIT_REQUESTS_PER_PAYLOAD: 8192
# 2**4 (= 16) withdrawal requests
MAX_WITHDRAWAL_REQUESTS_PER_PAYLOAD: 16
# 2**1 (= 2) consolidation requests
MAX_CONSOLIDATION_REQUESTS_PER_PAYLOAD: 2

# Withdrawals processing
# ---------------------------------------------------------------
# 2**3 (= 8) pending withdrawals
MAX_PENDING_PARTIALS_PER_WITHDRAWALS_SWEEP: 8

# Pending deposits processing
# ---------------------------------------------------------------
# 2**4 (= 16) pending deposits
MAX_PENDING_DEPOSITS_PER_EPOCH: 16
#### preset fulu
# Mainnet preset - Fulu

# Networking
# ---------------------------------------------------------------
# floorlog2(get_generalized_index(BeaconBlockBody, 'blob_kzg_commitments')) (= 4)
KZG_COMMITMENTS_INCLUSION_PROOF_DEPTH: 4

# Blob
# ---------------------------------------------------------------
# 2**6 (= 64) field elements
FIELD_ELEMENTS_PER_CELL: 64
# 2**1 * FIELD_ELEMENTS_PER_BLOB (= 8,192) field elements
FIELD_ELEMENTS_PER_EXT_BLOB: 8192
# FIELD_ELEMENTS_PER_EXT_BLOB // FIELD_ELEMENTS_PER_CELL (= 128) cells
CELLS_PER_EXT_BLOB: 128
# CELLS_PER_EXT_BLOB (= 128) columns
NUMBER_OF_COLUMNS: 128
#### preset gloas
# Mainnet preset - Gloas

# Misc
# ---------------------------------------------------------------
# 2**9 (= 512) validators
PTC_SIZE: 512

# Max operations per block
# ---------------------------------------------------------------
# 2**2 (= 4) attestations
MAX_PAYLOAD_ATTESTATIONS: 4

# Execution
# ---------------------------------------------------------------
# 2**6 (= 64) builder deposit requests
MAX_BUILDER_DEPOSIT_REQUESTS_PER_PAYLOAD: 64
# 2**4 (= 16) builder exit requests
MAX_BUILDER_EXIT_REQUESTS_PER_PAYLOAD: 16

# Withdrawals processing
# ---------------------------------------------------------------
# 2**14 (= 16,384) builders
MAX_BUILDERS_PER_WITHDRAWALS_SWEEP: 16384

# Type-specific SSZ bounds
# ---------------------------------------------------------------
# 16,829 bytes, ~16 KiB
MAX_SIGNED_AGGREGATE_AND_PROOF_SIZE: 16829
# 2,097,616 bytes, ~2 MiB
MAX_ATTESTER_SLASHING_SIZE: 2097616
# 196,932 bytes, ~192 KiB
MAX_SIGNED_EXECUTION_PAYLOAD_BID_SIZE: 196932
#### config
# Mainnet config

# Extends the mainnet preset
PRESET_BASE: 'mainnet'

# Free-form short name of the network that this configuration applies to - known
# canonical network names include:
# * 'mainnet' - there can be only one
# * 'sepolia' - testnet
# * 'holesky' - testnet
# * 'hoodi' - testnet
# Must match the regex: [a-z0-9\-]
CONFIG_NAME: 'mainnet'

# Transition
# ---------------------------------------------------------------
# Estimated on Sept 15, 2022
TERMINAL_TOTAL_DIFFICULTY: 58750000000000000000000
# By default, don't use these params
TERMINAL_BLOCK_HASH: 0x0000000000000000000000000000000000000000000000000000000000000000
TERMINAL_BLOCK_HASH_ACTIVATION_EPOCH: 18446744073709551615

# Genesis
# ---------------------------------------------------------------
# 2**14 (= 16,384) validators
MIN_GENESIS_ACTIVE_VALIDATOR_COUNT: 16384
# Dec 1, 2020, 12pm UTC
MIN_GENESIS_TIME: 1606824000
# Initial fork version for mainnet
GENESIS_FORK_VERSION: 0x00000000
# 7 * 24 * 3,600 (= 604,800) seconds, 7 days
GENESIS_DELAY: 604800

# Forking
# ---------------------------------------------------------------
# Some forks are disabled for now:
#  - These may be re-assigned to another fork-version later
#  - Temporarily set to max Uint64 value: 2**64 - 1

# Altair
ALTAIR_FORK_VERSION: 0x01000000
ALTAIR_FORK_EPOCH: 74240  # Oct 27, 2021, 10:56:23am UTC
# Bellatrix
BELLATRIX_FORK_VERSION: 0x02000000
BELLATRIX_FORK_EPOCH: 144896  # Sept 6, 2022, 11:34:47am UTC
# Capella
CAPELLA_FORK_VERSION: 0x03000000
CAPELLA_FORK_EPOCH: 194048  # April 12, 2023, 10:27:35pm UTC
# Deneb
DENEB_FORK_VERSION: 0x04000000
DENEB_FORK_EPOCH: 269568  # March 13, 2024, 01:55:35pm UTC
# Electra
ELECTRA_FORK_VERSION: 0x05000000
ELECTRA_FORK_EPOCH: 364032  # May 7, 2025, 10:05:11am UTC
# Fulu
FULU_FORK_VERSION: 0x06000000
FULU_FORK_EPOCH: 411392  # December 3, 2025, 09:49:11pm UTC
# Gloas
GLOAS_FORK_VERSION: 0x07000000
GLOAS_FORK_EPOCH: 18446744073709551615
# Heze
HEZE_FORK_VERSION: 0x08000000
HEZE_FORK_EPOCH: 18446744073709551615
# EIP8321
EIP8321_FORK_VERSION: 0xe8321000
EIP8321_FORK_EPOCH: 18446744073709551615

# Time parameters
# ---------------------------------------------------------------
# 12000 milliseconds
SLOT_DURATION_MS: 12000
# 14 (estimate from Eth1 mainnet)
SECONDS_PER_ETH1_BLOCK: 14
# 2**8 (= 256) epochs
MIN_VALIDATOR_WITHDRAWABILITY_DELAY: 256
# 2**8 (= 256) epochs
SHARD_COMMITTEE_PERIOD: 256
# 2**11 (= 2,048) Eth1 blocks
ETH1_FOLLOW_DISTANCE: 2048
# 1667 basis points, ~17% of SLOT_DURATION_MS
PROPOSER_REORG_CUTOFF_BPS: 1667
# 3333 basis points, ~33% of SLOT_DURATION_MS
ATTESTATION_DUE_BPS: 3333
# 6667 basis points, ~67% of SLOT_DURATION_MS
AGGREGATE_DUE_BPS: 6667

# Altair
# 3333 basis points, ~33% of SLOT_DURATION_MS
SYNC_MESSAGE_DUE_BPS: 3333
# 6667 basis points, ~67% of SLOT_DURATION_MS
CONTRIBUTION_DUE_BPS: 6667

# Gloas
# 2**6 (= 64) epochs
MIN_BUILDER_WITHDRAWABILITY_DELAY: 64
# 2500 basis points, 25% of SLOT_DURATION_MS
ATTESTATION_DUE_BPS_GLOAS: 2500
# 5000 basis points, 50% of SLOT_DURATION_MS
AGGREGATE_DUE_BPS_GLOAS: 5000
# 2500 basis points, 25% of SLOT_DURATION_MS
SYNC_MESSAGE_DUE_BPS_GLOAS: 2500
# 5000 basis points, 50% of SLOT_DURATION_MS
CONTRIBUTION_DUE_BPS_GLOAS: 5000
# 5000 basis points, 50% of SLOT_DURATION_MS
PAYLOAD_DUE_BPS: 5000
# 7500 basis points, 75% of SLOT_DURATION_MS
PAYLOAD_ATTESTATION_DUE_BPS: 7500

# Heze
# 6667 basis points, ~67% of SLOT_DURATION_MS
INCLUSION_LIST_DUE_BPS: 6667

# Validator cycle
# ---------------------------------------------------------------
# 2**2 (= 4)
INACTIVITY_SCORE_BIAS: 4
# 2**4 (= 16)
INACTIVITY_SCORE_RECOVERY_RATE: 16
# 2**4 * 10**9 (= 16,000,000,000) Gwei
EJECTION_BALANCE: 16000000000
# 2**2 (= 4) validators
MIN_PER_EPOCH_CHURN_LIMIT: 4
# 2**16 (= 65,536)
CHURN_LIMIT_QUOTIENT: 65536

# Deneb
# 2**3 (= 8) (*deprecated*)
MAX_PER_EPOCH_ACTIVATION_CHURN_LIMIT: 8

# Electra
# 2**7 * 10**9 (= 128,000,000,000) Gwei
MIN_PER_EPOCH_CHURN_LIMIT_ELECTRA: 128000000000
# 2**8 * 10**9 (= 256,000,000,000) Gwei
MAX_PER_EPOCH_ACTIVATION_EXIT_CHURN_LIMIT: 256000000000

# Gloas
# 2**15 (= 32,768)
CHURN_LIMIT_QUOTIENT_GLOAS: 32768
# 2**16 (= 65,536)
CONSOLIDATION_CHURN_LIMIT_QUOTIENT: 65536
# 2**8 * 10**9 (= 256,000,000,000) Gwei
MAX_PER_EPOCH_ACTIVATION_CHURN_LIMIT_GLOAS: 256000000000

# Fork choice
# ---------------------------------------------------------------
# 40%
PROPOSER_SCORE_BOOST: 40
# 20%
REORG_HEAD_WEIGHT_THRESHOLD: 20
# 160%
REORG_PARENT_WEIGHT_THRESHOLD: 160
# 2 epochs
REORG_MAX_EPOCHS_SINCE_FINALIZATION: 2

# Deposit contract
# ---------------------------------------------------------------
# Ethereum PoW Mainnet
DEPOSIT_CHAIN_ID: 1
DEPOSIT_NETWORK_ID: 1
DEPOSIT_CONTRACT_ADDRESS: 0x00000000219ab540356cBB839Cbe05303d7705Fa

# Networking
# ---------------------------------------------------------------
# 10 * 2**20 (= 10,485,760) bytes, 10 MiB
MAX_PAYLOAD_SIZE: 10485760
# 2**10 (= 1,024) blocks
MAX_REQUEST_BLOCKS: 1024
# 2**8 (= 256) epochs
EPOCHS_PER_SUBNET_SUBSCRIPTION: 256
# 2**5 (= 32) slots
ATTESTATION_PROPAGATION_SLOT_RANGE: 32
# 500ms
MAXIMUM_GOSSIP_CLOCK_DISPARITY: 500
MESSAGE_DOMAIN_INVALID_SNAPPY: 0x00000000
MESSAGE_DOMAIN_VALID_SNAPPY: 0x01000000
# 2 subnets per node
SUBNETS_PER_NODE: 2
# 2**6 (= 64) subnets
ATTESTATION_SUBNET_COUNT: 64
# 0 bits
ATTESTATION_SUBNET_EXTRA_BITS: 0

# Deneb
# 2**7 (= 128) blocks
MAX_REQUEST_BLOCKS_DENEB: 128
# 2**12 (= 4,096) epochs
MIN_EPOCHS_FOR_BLOB_SIDECARS_REQUESTS: 4096
# 6 subnets
BLOB_SIDECAR_SUBNET_COUNT: 6
# 6 blobs
MAX_BLOBS_PER_BLOCK: 6

# Electra
# 9 subnets
BLOB_SIDECAR_SUBNET_COUNT_ELECTRA: 9
# 9 blobs
MAX_BLOBS_PER_BLOCK_ELECTRA: 9

# Fulu
# 2**7 (= 128) groups
NUMBER_OF_CUSTODY_GROUPS: 128
# 2**7 (= 128) subnets
DATA_COLUMN_SIDECAR_SUBNET_COUNT: 128
# 2**3 (= 8) samples
SAMPLES_PER_SLOT: 8
# 2**2 (= 4) sidecars
CUSTODY_REQUIREMENT: 4
# 2**3 (= 8) sidecars
VALIDATOR_CUSTODY_REQUIREMENT: 8
# 2**5 * 10**9 (= 32,000,000,000) Gwei
BALANCE_PER_ADDITIONAL_CUSTODY_GROUP: 32000000000
# 2**12 (= 4,096) epochs
MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS: 4096

# Gloas
# 2**7 (= 128) payloads
MAX_REQUEST_PAYLOADS: 128

# Heze
# 2**4 (= 16) inclusion lists
MAX_REQUEST_INCLUSION_LIST: 16
# 1 slots
MIN_SLOTS_FOR_INCLUSION_LISTS_REQUESTS: 1
# 2**13 (= 8,192) bytes
MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST: 8192


# Scheduling
# ---------------------------------------------------------------

BLOB_SCHEDULE:
  - EPOCH: 412672  # December 9, 2025, 02:21:11pm UTC
    MAX_BLOBS_PER_BLOCK: 15
  - EPOCH: 419072  # January 7, 2026, 01:01:11am UTC
    MAX_BLOBS_PER_BLOCK: 21

GAS_LIMIT_SCHEDULE: []

# Fast Confirmation Rule
# ---------------------------------------------------------------
CONFIRMATION_BYZANTINE_THRESHOLD: 25

""";
}
