//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Cortex.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

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
            for (int index = 1; index < 32; index++)
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
            QuickStartParameters quickStartParameters = _quickStartParameterOptions.CurrentValue;

            if (_logger.IsWarn()) Log.MockedQuickStart(_logger, quickStartParameters.GenesisTime, quickStartParameters.ValidatorCount, null);

            GweiValues gweiValues = _gweiValueOptions.CurrentValue;
            InitialValues initialValues = _initialValueOptions.CurrentValue;
            SignatureDomains signatureDomains = _signatureDomainOptions.CurrentValue;

            // Fixed amount
            Gwei amount = gweiValues.MaximumEffectiveBalance;

            // Build deposits
            List<DepositData> depositDataList = new List<DepositData>();
            List<Deposit> deposits = new List<Deposit>();
            for (ulong validatorIndex = 0uL; validatorIndex < quickStartParameters.ValidatorCount; validatorIndex++)
            {
                byte[] privateKey = GeneratePrivateKey(validatorIndex);

                // Public Key
                BLSParameters blsParameters = new BLSParameters()
                {
                    PrivateKey = privateKey
                };
                using BLS bls = BLS.Create(blsParameters);
                byte[] publicKeyBytes = new byte[BlsPublicKey.Length];
                bls.TryExportBLSPublicKey(publicKeyBytes, out int publicKeyBytesWritten);
                BlsPublicKey publicKey = new BlsPublicKey(publicKeyBytes);

                // Withdrawal Credentials
                byte[] withdrawalCredentialBytes = _cryptographyService.Hash(publicKey.AsSpan()).AsSpan().ToArray();
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                Hash32 withdrawalCredentials = new Hash32(withdrawalCredentialBytes);

                // Build deposit data
                DepositData depositData = new DepositData(publicKey, withdrawalCredentials, amount);

                // Sign deposit data
                Hash32 depositDataSigningRoot = depositData.SigningRoot();
                Domain domain = _beaconChainUtility.ComputeDomain(signatureDomains.Deposit);
                byte[] destination = new byte[96];
                bls.TrySignHash(depositDataSigningRoot.AsSpan(), destination, out int bytesWritten, domain.AsSpan());
                BlsSignature depositDataSignature = new BlsSignature(destination);
                depositData.SetSignature(depositDataSignature);

                // Deposit

                // TODO: This seems a very inefficient way (copied from tests) as it recalculates the merkle tree each time
                // (you only need to add one node)

                // TODO: Add some tests around quick start, then improve

                int index = depositDataList.Count;
                depositDataList.Add(depositData);
                Hash32 root = depositDataList.HashTreeRoot((ulong)1 << _chainConstants.DepositContractTreeDepth);
                IEnumerable<Hash32> allLeaves = depositDataList.Select(x => x.HashTreeRoot());
                IList<IList<Hash32>> tree = CalculateMerkleTreeFromLeaves(allLeaves);
                

                IList<Hash32> merkleProof = GetMerkleProof(tree, index, 32);
                List<Hash32> proof = new List<Hash32>(merkleProof);
                Span<byte> indexBytes = new Span<byte>(new byte[32]);
                BitConverter.TryWriteBytes(indexBytes, (ulong)index + 1);
                if (!BitConverter.IsLittleEndian)
                {
                    indexBytes.Slice(0, 8).Reverse();
                }

                Hash32 indexHash = new Hash32(indexBytes);
                proof.Add(indexHash);
                Hash32 leaf = depositData.HashTreeRoot();
                _beaconChainUtility.IsValidMerkleBranch(leaf, proof, _chainConstants.DepositContractTreeDepth + 1, (ulong)index, root);
                Deposit deposit = new Deposit(proof, depositData);

                if (_logger.IsEnabled(LogLevel.Debug))
                    LogDebug.QuickStartAddValidator(_logger, validatorIndex, publicKey.ToString().Substring(0, 12),
                        null);

                deposits.Add(deposit);
            }

            BeaconState genesisState = _beaconChain.InitializeBeaconStateFromEth1(quickStartParameters.Eth1BlockHash, quickStartParameters.Eth1Timestamp, deposits);
            // We use the state directly, and don't test IsValid
            genesisState.SetGenesisTime(quickStartParameters.GenesisTime);
            IStore store = _forkChoice.GetGenesisStore(genesisState);

            if (_logger.IsEnabled(LogLevel.Debug)) LogDebug.QuickStartStoreCreated(_logger, store.GenesisTime, null);
        }

        private static IList<IList<Hash32>> CalculateMerkleTreeFromLeaves(IEnumerable<Hash32> values, int layerCount = 32)
        {
            List<Hash32> workingValues = new List<Hash32>(values);
            List<IList<Hash32>> tree = new List<IList<Hash32>>(new[] { workingValues.ToArray() });
            for (int height = 0; height < layerCount; height++)
            {
                if (workingValues.Count % 2 == 1)
                {
                    workingValues.Add(new Hash32(s_zeroHashes[height]));
                }
                List<Hash32> hashes = new List<Hash32>();
                for (int index = 0; index < workingValues.Count; index += 2)
                {
                    byte[] hash = Hash(workingValues[index].AsSpan(), workingValues[index + 1].AsSpan());
                    hashes.Add(new Hash32(hash));
                }
                tree.Add(hashes.ToArray());
                workingValues = hashes;
            }
            return tree;
        }

        private static IList<Hash32> GetMerkleProof(IList<IList<Hash32>> tree, int itemIndex, int? treeLength = null)
        {
            List<Hash32> proof = new List<Hash32>();
            for (int height = 0; height < (treeLength ?? tree.Count); height++)
            {
                int subindex = (itemIndex / (1 << height)) ^ 1;
                Hash32 value = subindex < tree[height].Count
                    ? tree[height][subindex]
                    : new Hash32(s_zeroHashes[height]);
                proof.Add(value);
            }
            return proof;
        }

        private static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            Span<byte> combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            return s_hashAlgorithm.ComputeHash(combined.ToArray());
        }

        public byte[] GeneratePrivateKey(ulong index)
        {
            Span<byte> input = new Span<byte>(new byte[32]);
            BigInteger bigIndex = new BigInteger(index);
            bool indexWriteSuccess = bigIndex.TryWriteBytes(input, out int indexBytesWritten, isUnsigned: true, isBigEndian: false);
            if (!indexWriteSuccess || indexBytesWritten == 0)
            {
                throw new Exception("Error getting input for quick start private key generation.");
            }

            Hash32 hash32 = _cryptographyService.Hash(input);
            ReadOnlySpan<byte> hash = hash32.AsSpan();
            // Mocked start interop specifies to convert the hash as little endian (which is the default for BigInteger)
            BigInteger value = new BigInteger(hash.ToArray(), isUnsigned: true);
            BigInteger privateKey = value % s_curveOrder;

            // Note that the private key is an *unsigned*, *big endian* number
            // However, we want to pad the big endian on the left to get 32 bytes.
            // So, write as little endian (will pad to right), then reverse.
            // NOTE: Alternative, write to Span 64, and then slice based on bytesWritten to get the padding.
            Span<byte> privateKeySpan = new Span<byte>(new byte[32]);
            bool keyWriteSuccess = privateKey.TryWriteBytes(privateKeySpan, out int keyBytesWritten, isUnsigned: true, isBigEndian: false);
            if (!keyWriteSuccess)
            {
                throw new Exception("Error generating quick start private key.");
            }
            privateKeySpan.Reverse();
            
            return privateKeySpan.ToArray();
        }
    }
}
