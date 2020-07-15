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
using System.Diagnostics;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.HashLib;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Transactions
{
    /// <summary>
    /// A production implementation of a randomness contract can be found here:
    /// https://github.com/poanetwork/posdao-contracts/blob/master/contracts/RandomAuRa.sol
    /// </summary>
    public class RandomContractTxSource : ITxSource
    {
        private readonly IEciesCipher _eciesCipher;
        private readonly ProtectedPrivateKey _cryptoKey;
        private readonly IList<IRandomContract> _contracts;
        private readonly ICryptoRandom _random;
        private readonly ILogger _logger;

        public RandomContractTxSource(
            IList<IRandomContract> contracts,
            IEciesCipher eciesCipher,
            ProtectedPrivateKey cryptoKey, 
            ICryptoRandom cryptoRandom,
            ILogManager logManager)
        {
            _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
            _eciesCipher = eciesCipher ?? throw new ArgumentNullException(nameof(eciesCipher));
            _cryptoKey = cryptoKey ?? throw new ArgumentNullException(nameof(cryptoKey));
            _random = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _logger = logManager?.GetClassLogger<RandomContractTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            if (_lastTransactionInfo != null && _lastTransactionInfo.ParentBlockHash == parent.Hash)
            {
                if (_logger.IsWarn) _logger.Warn($"Trying to produce multiple RANDAO transactions on same block {parent.Number + 1} on phase {_lastTransactionInfo.Phase}. " +
                                                 $"First transaction produced by object_{_lastTransactionInfo.Id} on stack trace {_lastTransactionInfo.StackTrace}. " +
                                                 $"Second transaction produced by object_{_id} on stack trace {new StackTrace()}");
                yield break;
            }
            
            if (_contracts.TryGetForBlock(parent.Number + 1, out var contract))
            {
                var (phase, tx) = GetTransaction(contract, parent);
                if (tx != null && tx.GasLimit <= gasLimit)
                {
                    _lastTransactionInfo = new TransactionInfo()
                    {
                        ParentBlockHash = parent.Hash,
                        Phase = phase,
                        StackTrace = new StackTrace(),
                        Id = _id
                    };
                    
                    yield return tx;
                }
            }
        }

        private (IRandomContract.Phase, Transaction) GetTransaction(in IRandomContract contract, in BlockHeader parent)
        {
            var (phase, round) = contract.GetPhase(parent);
            switch (phase)
            {
                case IRandomContract.Phase.BeforeCommit:
                {
                    byte[] bytes = new byte[32];
                    _random.GenerateRandomBytes(bytes);
                    var hash = Keccak.Compute(bytes);
                    var cipher = _eciesCipher.Encrypt(_cryptoKey.PublicKey, bytes);
                    return (phase, contract.CommitHash(hash, cipher));
                }
                case IRandomContract.Phase.Reveal:
                {
                    var (hash, cipher) = contract.GetCommitAndCipher(parent, round);
                    using PrivateKey privateKey = _cryptoKey.Unprotect();
                    byte[] bytes = _eciesCipher.Decrypt(privateKey, cipher).Item2;
                    if (bytes?.Length != 32)
                    {
                        // This can only happen if there is a bug in the smart contract, or if the entire network goes awry.
                        throw new AuRaException("Decrypted random number has the wrong length.");
                    }
                    
                    var computedHash = ValueKeccak.Compute(bytes);
                    if (!Bytes.AreEqual(hash.Bytes, computedHash.BytesAsSpan))
                    {
                        throw new AuRaException("Decrypted random number doesn't agree with the hash.");
                    }
                    
                    UInt256.CreateFromBigEndian(out var number, bytes);
                    
                    return (phase, contract.RevealNumber(number));
                }
            }
            
            return (phase, null);
        }
        
        private static TransactionInfo _lastTransactionInfo = new TransactionInfo();

        private class TransactionInfo
        {
            public Keccak ParentBlockHash { get; set; }
            public StackTrace StackTrace { get; set; }
            public IRandomContract.Phase Phase { get; set; }
            public int Id { get; set; }
        }

        private readonly int _id = ITxSource.IdCounter;
        public override string ToString() => $"{GetType().Name}_{_id}";
    }
}
