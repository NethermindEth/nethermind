using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Cortex.Cryptography;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    public class BeaconChain
    {
        private const int DEPOSIT_CONTRACT_LIMIT = 2 ^ DEPOSIT_CONTRACT_TREE_DEPTH;
        private const int DEPOSIT_CONTRACT_TREE_DEPTH = 2 ^ 5; // 32
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();
        private static readonly Gwei EFFECTIVE_BALANCE_INCREMENT = (2 ^ 0) * (10 ^ 9);
        private static readonly Epoch FAR_FUTURE_EPOCH = 2 ^ 64 - 1;

        private static readonly Gwei MAX_EFFECTIVE_BALANCE = (2 ^ 5) * (10 ^ 9); // 32,000,000,000
        private readonly BeaconChainParameters _beaconChainParameters;

        private readonly InitialValues _initialValues;

        // 1,000,000,000
        private readonly ILogger _logger;
        private readonly BlsSignatureService _blsSignatureService;
        private readonly MaxOperationsPerBlock _maxOperationsPerBlock;
        private readonly TimeParameters _timeParameters;

        public BeaconChain(ILogger<BeaconChain> logger,
            BlsSignatureService blsSignatureService,
            BeaconChainParameters beaconChainParameters,
            InitialValues initialValues,
            TimeParameters timeParameters,
            MaxOperationsPerBlock maxOperationsPerBlock)
        {
            _logger = logger;
            _blsSignatureService = blsSignatureService;
            _beaconChainParameters = beaconChainParameters;
            _initialValues = initialValues;
            _timeParameters = timeParameters;
            _maxOperationsPerBlock = maxOperationsPerBlock;
        }

        public BeaconBlock? GenesisBlock { get; private set; }
        public BeaconState? GenesisState { get; private set; }
        public BeaconState? State { get; private set; }

        /// <summary>
        /// Returns the domain for the 'domain_type' and 'fork_version'
        /// </summary>
        public Domain ComputeDomain(Domain domainType, ForkVersion forkVersion)
        {
            var combined = new Span<byte>(new byte[Domain.Length]);
            domainType.AsSpan().CopyTo(combined);
            forkVersion.AsSpan().CopyTo(combined.Slice(DomainType.Length));
            return combined;
        }

        public BeaconState InitializeBeaconStateFromEth1(Hash32 eth1BlockHash, ulong eth1Timestamp, IEnumerable<Deposit> deposits)
        {
            var genesisTime = eth1Timestamp - (eth1Timestamp % _timeParameters.SecondsPerDay) + (2 * _timeParameters.SecondsPerDay);
            var eth1Data = new Eth1Data(eth1BlockHash, (ulong)deposits.Count());
            var emptyBlockBody = new BeaconBlockBody();
            var latestBlockHeader = new BeaconBlockHeader(emptyBlockBody.HashTreeRoot(_maxOperationsPerBlock));
            var state = new BeaconState(genesisTime, eth1Data, latestBlockHeader);

            // Process deposits
            var depositDataList = new List<DepositData>();
            foreach (var deposit in deposits)
            {
                depositDataList.Add(deposit.Data);
                var depositRoot = depositDataList.HashTreeRoot(DEPOSIT_CONTRACT_LIMIT);
                state.Eth1Data.SetDepositRoot(depositRoot);
                ProcessDeposit(state, deposit);
            }

            // Process activations
            // TODO:

            return state;
        }

        public bool IsValidGenesisState(BeaconState state)
        {
            if (state.GenesisTime < _beaconChainParameters.MinGenesisTime)
            {
                return false;
            }
            var activeValidatorIndices = state.GetActiveValidatorIndices(_initialValues.GenesisEpoch);
            if (activeValidatorIndices.Count < _beaconChainParameters.MinGenesisActiveValidatorCount)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Check if 'leaf' at 'index' verifies against the Merkle 'root' and 'branch'
        /// </summary>
        public bool IsValidMerkleBranch(Hash32 leaf, IReadOnlyList<Hash32> branch, int depth, ulong index, Hash32 root)
        {
            var value = leaf;
            for (var testDepth = 0; testDepth < depth; testDepth++)
            {
                var branchValue = branch[testDepth];
                var indexAtDepth = index / (2 ^ (ulong)testDepth);
                if (indexAtDepth % 2 == 0)
                {
                    // Branch on right
                    value = Hash(value, branchValue);
                }
                else
                {
                    // Branch on left
                    value = Hash(branchValue, value);
                }
            }
            return value.Equals(root);
        }

        public void ProcessDeposit(BeaconState state, Deposit deposit)
        {
            // Verify the Merkle branch
            var isValid = IsValidMerkleBranch(
                deposit.Data.HashTreeRoot(),
                deposit.Proof,
                DEPOSIT_CONTRACT_TREE_DEPTH + 1, // Add 1 for the 'List' length mix-in
                state.Eth1DepositIndex,
                state.Eth1Data.DepositRoot);

            // Deposits must be processed in order
            state.IncreaseEth1DepositIndex();

            var publicKey = deposit.Data.PublicKey;
            var amount = deposit.Data.Amount;
            var validatorPublicKeys = state.Validators.Select(x => x.PublicKey).ToList();

            if (!validatorPublicKeys.Contains(publicKey))
            {
                // Verify the deposit signature (proof of possession) for new validators
                // Note: The deposit contract does not check signatures.
                // Note: Deposits are valid across forks, thus the deposit domain is retrieved directly from 'computer_domain'.

                Domain domain = ComputeDomain(Domain.Deposit, new ForkVersion());
                if (!_blsSignatureService.BlsVerify(publicKey, deposit.Data.SigningRoot(), deposit.Data.Signature, domain))
                {
                    return;
                }

                var effectiveBalance = Math.Min(amount - amount % EFFECTIVE_BALANCE_INCREMENT, MAX_EFFECTIVE_BALANCE);
                var newValidator = new Validator(
                    publicKey,
                    deposit.Data.WithdrawalCredentials,
                    FAR_FUTURE_EPOCH,
                    FAR_FUTURE_EPOCH,
                    FAR_FUTURE_EPOCH,
                    FAR_FUTURE_EPOCH,
                    effectiveBalance
                    );
                state.AddValidator(newValidator);
            }
            else
            {
                var index = (ValidatorIndex)(ulong)validatorPublicKeys.IndexOf(publicKey);
                state.IncreaseBalance(index, amount);
            }
        }

        public async Task<bool> TryGenesisAsync(Hash32 eth1BlockHash, ulong eth1Timestamp, IList<Deposit> deposits)
        {
            return await Task.Run(() =>
            {
                var candidateState = InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
                if (IsValidGenesisState(candidateState))
                {
                    GenesisState = candidateState;
                    GenesisBlock = new BeaconBlock(GenesisState.HashTreeRoot());
                    return true;
                }
                return false;
            });
        }

        private Hash32 Hash(Hash32 a, Hash32 b)
        {
            var input = new Span<byte>(new byte[64]);
            a.AsSpan().CopyTo(input);
            b.AsSpan().CopyTo(input.Slice(32));
            var result = new Span<byte>(new byte[32]);
            var success = _hashAlgorithm.TryComputeHash(input, result, out var bytesWritten);
            if (!success || bytesWritten != 32)
            {
                throw new InvalidOperationException("Error generating hash value.");
            }
            return result;
        }

        // Update store via... (store processor ?)

        // on_tick

        // on_block(store, block)
        //          state_transition(pre_state, block)

        // on_attestation
    }
}
