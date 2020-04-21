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
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class RandomImmediateTransactionSource : IImmediateTransactionSource
    {
        private readonly Address _nodeAddress;
        private readonly IList<RandomContract> _contracts;
        private readonly Random _random = new Random();

        public RandomImmediateTransactionSource(
            IDictionary<long, Address> randomnessContractAddress, 
            ITransactionProcessor transactionProcessor, 
            IAbiEncoder abiEncoder, 
            IStateProvider stateProvider, 
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource,
            Address nodeAddress)
        {
            _nodeAddress = nodeAddress ?? throw new ArgumentNullException(nameof(nodeAddress));
            _contracts = randomnessContractAddress
                .Select(kvp => new RandomContract(transactionProcessor, abiEncoder, kvp.Value, stateProvider, readOnlyTransactionProcessorSource, kvp.Key))
                .ToList();
        }
        
        public bool TryCreateTransaction(BlockHeader parent, long gasLimit, out Transaction tx)
        {
            tx = _contracts.TryGetForBlock(parent.Number + 1, out var contract)
                ? GetTransaction(contract, parent)
                : null;
            
            return tx != null;
        }

        private Transaction GetTransaction(in RandomContract contract, in BlockHeader parent)
        {
            var (phase, round) = contract.GetPhase(parent, _nodeAddress);
            switch (phase)
            {
                case RandomContract.Phase.BeforeCommit:
                {
                    Span<byte> bytes = stackalloc byte[32];
                    _random.NextBytes(bytes);
                    // UInt256.CreateFromBigEndian(out var number, bytes);
                    var hash = Keccak.Compute(bytes);
                    byte[] cipher = bytes.ToArray(); // TODO: ENCRYPT!
                    return contract.CommitHash(hash, cipher);
                }
                case RandomContract.Phase.Reveal:
                {
                    var (hash, cipher) = contract.GetCommitAndCipher(parent, round, _nodeAddress);
                    byte[] bytes = cipher;// TODO: decrypt cipher!
                    if (bytes.Length != 32)
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
                    
                    return contract.RevealNumber(number);
                }
                case RandomContract.Phase.Waiting:
                case RandomContract.Phase.Committed:
                    return null;
            }
            
            return null;
        }
    }
}