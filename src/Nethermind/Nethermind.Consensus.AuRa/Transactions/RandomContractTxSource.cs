// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Org.BouncyCastle.Crypto;

namespace Nethermind.Consensus.AuRa.Transactions
{
    /// <summary>
    /// A production implementation of a randomness contract can be found here:
    /// https://github.com/poanetwork/posdao-contracts/blob/master/contracts/RandomAuRa.sol
    /// </summary>
    public class RandomContractTxSource : ITxSource
    {
        private readonly IEciesCipher _eciesCipher;
        private readonly ISigner _signer;
        private readonly ProtectedPrivateKey _previousCryptoKey;
        private readonly IList<IRandomContract> _contracts;
        private readonly ICryptoRandom _random;
        private readonly ILogger _logger;

        public RandomContractTxSource(
            IList<IRandomContract> contracts,
            IEciesCipher eciesCipher,
            ISigner signer,
            ProtectedPrivateKey previousCryptoKey, // this is for backwards-compability when upgrading validator node 
            ICryptoRandom cryptoRandom,
            ILogManager logManager)
        {
            _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
            _eciesCipher = eciesCipher ?? throw new ArgumentNullException(nameof(eciesCipher));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _previousCryptoKey = previousCryptoKey ?? throw new ArgumentNullException(nameof(previousCryptoKey));
            _random = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _logger = logManager?.GetClassLogger<RandomContractTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            if (_contracts.TryGetForBlock(parent.Number + 1, out var contract))
            {
                Transaction? tx = GetTransaction(contract, parent);
                if (tx is not null)
                {
                    yield return tx;
                }
            }
        }

        private Transaction? GetTransaction(in IRandomContract contract, in BlockHeader parent)
        {
            try
            {
                var (phase, round) = contract.GetPhase(parent);
                switch (phase)
                {
                    case IRandomContract.Phase.BeforeCommit:
                        {
                            byte[] bytes = new byte[32];
                            _random.GenerateRandomBytes(bytes);
                            var hash = Keccak.Compute(bytes);
                            PrivateKey? privateKey = _signer.Key;
                            if (privateKey is not null)
                            {
                                var cipher = _eciesCipher.Encrypt(privateKey.PublicKey, bytes);
                                Metrics.CommitHashTransaction++;
                                return contract.CommitHash(hash, cipher);
                            }

                            return null;
                        }
                    case IRandomContract.Phase.Reveal:
                        {
                            var (hash, cipher) = contract.GetCommitAndCipher(parent, round);
                            byte[] bytes;
                            try
                            {
                                PrivateKey privateKey = _signer.Key;
                                if (privateKey is not null)
                                {
                                    using (privateKey)
                                    {
                                        bytes = _eciesCipher.Decrypt(privateKey, cipher).Item2;
                                    }
                                }
                                else
                                {
                                    return null;
                                }
                            }
                            catch (InvalidCipherTextException)
                            {
                                // Before we used node key here, now we want to use signer key. So we can move signer to other node.
                                // But we need to fallback to node key here when we upgrade version.
                                // This is temporary code after all validators are upgraded we can remove it.
                                using PrivateKey privateKey = _previousCryptoKey.Unprotect();
                                bytes = _eciesCipher.Decrypt(privateKey, cipher).Item2;
                            }

                            if (bytes?.Length != 32)
                            {
                                // This can only happen if there is a bug in the smart contract, or if the entire network goes awry.
                                throw new AuRaException("Decrypted random number has the wrong length.");
                            }

                            var computedHash = ValueKeccak.Compute(bytes);
                            if (!Bytes.AreEqual(hash.Span, computedHash.Span))
                            {
                                throw new AuRaException("Decrypted random number doesn't agree with the hash.");
                            }

                            UInt256 number = new UInt256(bytes, true);

                            Metrics.RevealNumber++;
                            return contract.RevealNumber(number);
                        }
                }
            }
            catch (AuRaException e)
            {
                if (_logger.IsError) _logger.Error($"RANDAO Failed on block {parent.ToString(BlockHeader.Format.FullHashAndNumber)} {new StackTrace()}", e);
            }
            catch (AbiException e)
            {
                if (_logger.IsError) _logger.Error($"RANDAO Failed on block {parent.ToString(BlockHeader.Format.FullHashAndNumber)} {new StackTrace()}", e);
            }

            return null;
        }

        public override string ToString() => $"{nameof(RandomContractTxSource)}";
    }
}
