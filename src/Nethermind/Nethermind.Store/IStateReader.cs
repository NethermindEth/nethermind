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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Store
{
    public interface IStateReader
    {
        bool AccountExists(Keccak stateRoot, Address address);

        bool IsDeadAccount(Keccak stateRoot, Address address);

        bool IsEmptyAccount(Keccak stateRoot, Address address);

        Account GetAccount(Keccak stateRoot, Address address);
        
        UInt256 GetNonce(Keccak stateRoot, Address address);

        UInt256 GetBalance(Keccak stateRoot, Address address);
        
        Keccak GetStorageRoot(Keccak stateRoot, Address address);

        Keccak GetCodeHash(Keccak stateRoot, Address address);
        
        byte[] GetCode(Keccak stateRoot, Address address);

        byte[] GetCode(Keccak codeHash);
        
        void RunTreeVisitor(Keccak stateRoot, ITreeVisitor treeVisitor);
    }
}