/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Store
{
    public class StateReader : IStateReader
    {
        private readonly ILogger _logger;
        
        private readonly IDb _codeDb;

        public StateReader(ISnapshotableDb stateDb, IDb codeDb, ILogManager logManager)
        {
            if (stateDb == null) throw new ArgumentNullException(nameof(stateDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _state = new StateTree(stateDb);
        }
        
        private readonly StateTree _state;

        public bool AccountExists(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address) != null;
        }

        public bool IsEmptyAccount(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address).IsEmpty;
        }

        public Account GetAccount(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address);
        }

        public bool IsDeadAccount(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address)?.IsEmpty ?? true;
        }

        public UInt256 GetNonce(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address)?.Nonce ?? UInt256.Zero;
        }

        public Keccak GetStorageRoot(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address).StorageRoot;
        }

        public UInt256 GetBalance(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address)?.Balance ?? UInt256.Zero;
        }

        public Keccak GetCodeHash(Keccak rootHash, Address address)
        {
            return GetState(rootHash, address)?.CodeHash ?? Keccak.OfAnEmptyString;
        }

        public byte[] GetCode(Keccak codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString)
            {
                return new byte[0];
            }

            return _codeDb[codeHash.Bytes];
        }

        public void RunTreeVisitor(Keccak rootHash, ITreeVisitor treeVisitor)	
        {
            _state.Accept(treeVisitor, _codeDb, rootHash);	
        }

        public byte[] GetCode(Keccak rootHash, Address address)
        {
            Account account = GetState(rootHash, address);
            if (account == null)
            {
                return new byte[0];
            }

            return GetCode(account.CodeHash);
        }

        private Account GetState(Keccak rootHash, Address address)
        {
            Metrics.StateTreeReads++;
            Account account = _state.Get(address, rootHash);
            return account;
        }
    }
}