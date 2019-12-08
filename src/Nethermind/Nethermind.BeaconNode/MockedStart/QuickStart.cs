using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Cortex.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.MockedStart
{
    public class QuickStart : INodeStart
    {
        private static readonly BigInteger s_curveOrder = BigInteger.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513");

        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();

        private static readonly byte[][] s_zeroHashes = new byte[32][];

        private readonly Genesis _beaconChain;
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _cryptographyService;
        private readonly ForkChoice _forkChoice;
        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger<QuickStart> _logger;
        private readonly IOptionsMonitor<QuickStartParameters> _quickStartParameterOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;

        static QuickStart()
        {
            s_zeroHashes[0] = new byte[32];
            for (var index = 1; index < 32; index++)
            {
                s_zeroHashes[index] = Hash(s_zeroHashes[index - 1], s_zeroHashes[index - 1]);
            }
        }

        public QuickStart(ILogger<QuickStart> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            IOptionsMonitor<QuickStartParameters> quickStartParameterOptions,
            ICryptographyService cryptographyService,
            BeaconChainUtility beaconChainUtility,
            Genesis beaconChain,
            ForkChoice forkChoice)
        {
            _logger = logger;
            _chainConstants = chainConstants;
            _gweiValueOptions = gweiValueOptions;
            _initialValueOptions = initialValueOptions;
            _signatureDomainOptions = signatureDomainOptions;
            _quickStartParameterOptions = quickStartParameterOptions;
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _beaconChain = beaconChain;
            _forkChoice = forkChoice;
        }

        public Task InitializeNodeAsync()
        {
            return Task.Run(QuickStartGenesis);
        }

        public void QuickStartGenesis()
        {
            var quickStartParameters = _quickStartParameterOptions.CurrentValue;

            _logger.LogWarning(0, "Mocked quick start with genesis time {GenesisTime} and {ValidatorCount} validators.",
                quickStartParameters.GenesisTime, quickStartParameters.ValidatorCount);

            var gweiValues = _gweiValueOptions.CurrentValue;
            var initialValues = _initialValueOptions.CurrentValue;
            var signatureDomains = _signatureDomainOptions.CurrentValue;

            // Fixed amount
            var amount = gweiValues.MaximumEffectiveBalance;

            // Build deposits
            var depositDataList = new List<DepositData>();
            var deposits = new List<Deposit>();
            for (var validatorIndex = 0uL; validatorIndex < quickStartParameters.ValidatorCount; validatorIndex++)
            {
                var privateKey = GeneratePrivateKey(validatorIndex);

                // Public Key
                var blsParameters = new BLSParameters()
                {
                    PrivateKey = privateKey
                };
                using var bls = BLS.Create(blsParameters);
                var publicKeyBytes = new byte[BlsPublicKey.Length];
                bls.TryExportBLSPublicKey(publicKeyBytes, out var publicKeyBytesWritten);
                var publicKey = new BlsPublicKey(publicKeyBytes);

                // Withdrawal Credentials
                var withdrawalCredentialBytes = _cryptographyService.Hash(publicKey.AsSpan()).AsSpan().ToArray();
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);

                // Build deposit data
                var depositData = new DepositData(publicKey, withdrawalCredentials, amount);

                // Sign deposit data
                var depositDataSigningRoot = depositData.SigningRoot();
                var domain = _beaconChainUtility.ComputeDomain(signatureDomains.Deposit);
                var destination = new byte[96];
                bls.TrySignHash(depositDataSigningRoot.AsSpan(), destination, out var bytesWritten, domain.AsSpan());
                var depositDataSignature = new BlsSignature(destination);
                depositData.SetSignature(depositDataSignature);

                // Deposit

                // TODO: This seems a very inefficient way (copied from tests) as it recalculates the merkle tree each time
                // (you only need to add one node)

                // TODO: Add some tests around quick start, then improve

                var index = depositDataList.Count;
                depositDataList.Add(depositData);
                var root = depositDataList.HashTreeRoot((ulong)1 << _chainConstants.DepositContractTreeDepth);
                var allLeaves = depositDataList.Select(x => x.HashTreeRoot());
                var tree = CalculateMerkleTreeFromLeaves(allLeaves);
                var merkleProof = GetMerkleProof(tree, index, 32);
                var proof = new List<Hash32>(merkleProof);
                var indexBytes = new Span<byte>(new byte[32]);
                BitConverter.TryWriteBytes(indexBytes, (ulong)index + 1);
                if (!BitConverter.IsLittleEndian)
                {
                    indexBytes.Slice(0, 8).Reverse();
                }
                var indexHash = new Hash32(indexBytes);
                proof.Add(indexHash);
                var leaf = depositData.HashTreeRoot();
                _beaconChainUtility.IsValidMerkleBranch(leaf, proof, _chainConstants.DepositContractTreeDepth + 1, (ulong)index, root);
                var deposit = new Deposit(proof, depositData);

                _logger.LogDebug("Quick start adding deposit for mocked validator {ValidatorIndex} with public key {PublicKey}.",
                    validatorIndex, publicKey);

                deposits.Add(deposit);
            }

            var genesisState = _beaconChain.InitializeBeaconStateFromEth1(quickStartParameters.Eth1BlockHash, quickStartParameters.Eth1Timestamp, deposits);
            // We use the state directly, and don't test IsValid
            genesisState.SetGenesisTime(quickStartParameters.GenesisTime);
            var store = _forkChoice.GetGenesisStore(genesisState);

            _logger.LogDebug("Quick start genesis store created with genesis time {GenesisTime}.", store.GenesisTime);
        }

        private static IList<IList<Hash32>> CalculateMerkleTreeFromLeaves(IEnumerable<Hash32> values, int layerCount = 32)
        {
            var workingValues = new List<Hash32>(values);
            var tree = new List<IList<Hash32>>(new[] { workingValues.ToArray() });
            for (var height = 0; height < layerCount; height++)
            {
                if (workingValues.Count % 2 == 1)
                {
                    workingValues.Add(new Hash32(s_zeroHashes[height]));
                }
                var hashes = new List<Hash32>();
                for (var index = 0; index < workingValues.Count; index += 2)
                {
                    var hash = Hash(workingValues[index].AsSpan(), workingValues[index + 1].AsSpan());
                    hashes.Add(new Hash32(hash));
                }
                tree.Add(hashes.ToArray());
                workingValues = hashes;
            }
            return tree;
        }

        private static IList<Hash32> GetMerkleProof(IList<IList<Hash32>> tree, int itemIndex, int? treeLength = null)
        {
            var proof = new List<Hash32>();
            for (var height = 0; height < (treeLength ?? tree.Count); height++)
            {
                var subindex = (itemIndex / (1 << height)) ^ 1;
                var value = subindex < tree[height].Count
                    ? tree[height][subindex]
                    : new Hash32(s_zeroHashes[height]);
                proof.Add(value);
            }
            return proof;
        }

        private static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            return s_hashAlgorithm.ComputeHash(combined.ToArray());
        }

        private byte[] GeneratePrivateKey(ulong index)
        {
            var input = new Span<byte>(new byte[32]);
            var bigIndex = new BigInteger(index);
            var indexWriteSuccess = bigIndex.TryWriteBytes(input, out var indexBytesWritten);
            if (!indexWriteSuccess || indexBytesWritten == 0)
            {
                throw new Exception("Error getting input for quick start private key generation.");
            }

            var hash32 = _cryptographyService.Hash(input);
            var hash = hash32.AsSpan();
            // Mocked start interop specifies to convert the hash as little endian (which is the default for BigInteger)
            var value = new BigInteger(hash.ToArray(), isUnsigned: true);
            var privateKey = value % s_curveOrder;

            // Note that the private key is an *unsigned*, *big endian* number
            var privateKeySpan = new Span<byte>(new byte[32]);
            var keyWriteSuccess = privateKey.TryWriteBytes(privateKeySpan, out var keyBytesWritten, isUnsigned: true, isBigEndian: true);
            if (!keyWriteSuccess || keyBytesWritten != 32)
            {
                throw new Exception("Error generating quick start private key.");
            }

            return privateKeySpan.ToArray();
        }
    }
}
