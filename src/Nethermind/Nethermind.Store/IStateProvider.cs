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

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Store
{
    public interface IStateProvider : ISnapshotable
    {
        Keccak StateRoot { get; set; }

        void DeleteAccount(Address address);

        void CreateAccount(Address address, BigInteger balance);

        bool AccountExists(Address address);

        bool IsDeadAccount(Address address);

        bool IsEmptyAccount(Address address);

        BigInteger GetNonce(Address address);

        BigInteger GetBalance(Address address);
        
        Keccak GetStorageRoot(Address address);

        byte[] GetCode(Address address);

        byte[] GetCode(Keccak codeHash);

        void UpdateCodeHash(Address address, Keccak codeHash, IReleaseSpec spec);

        void UpdateBalance(Address address, BigInteger balanceChange, IReleaseSpec spec);

        void UpdateStorageRoot(Address address, Keccak storageRoot);

        void IncrementNonce(Address address);

        Keccak UpdateCode(byte[] code);

        void ClearCaches(); // TODO: temp while designing DB <-> store interaction
    }
}