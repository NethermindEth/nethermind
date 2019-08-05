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
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.Clique
{
    public class CliqueModule : ICliqueModule
    {
        private readonly ICliqueBridge _cliqueBridge;

        public CliqueModule(ILogManager logManager, ICliqueBridge cliqueBridge)
        {
            _cliqueBridge = cliqueBridge ?? throw new ArgumentNullException(nameof(cliqueBridge));
        }

        public ResultWrapper<Snapshot> clique_getSnapshot()
        {
            return ResultWrapper<Snapshot>.Success(_cliqueBridge.GetSnapshot());
        }

        public ResultWrapper<Snapshot> clique_getSnapshotAtHash(Keccak hash)
        {
            return ResultWrapper<Snapshot>.Success(_cliqueBridge.GetSnapshot(hash));
        }

        public ResultWrapper<Address[]> clique_getSigners()
        {
            return ResultWrapper<Address[]>.Success(_cliqueBridge.GetSigners().ToArray());
        }

        public ResultWrapper<Address[]> clique_getSignersAtHash(Keccak hash)
        {
            return ResultWrapper<Address[]>.Success(_cliqueBridge.GetSigners(hash).ToArray());
        }
        
        public ResultWrapper<Address[]> clique_getSignersAtNumber(long number)
        {
            return ResultWrapper<Address[]>.Success(_cliqueBridge.GetSigners(number).ToArray());
        }
        
        public ResultWrapper<string[]> clique_getSignersAnnotated()
        {
            return ResultWrapper<string[]>.Success(_cliqueBridge.GetSignersAnnotated().ToArray());
        }

        public ResultWrapper<string[]> clique_getSignersAtHashAnnotated(Keccak hash)
        {
            return ResultWrapper<string[]>.Success(_cliqueBridge.GetSignersAnnotated(hash).ToArray());
        }

        public ResultWrapper<bool> clique_propose(Address signer, bool vote)
        {
            try
            {
                _cliqueBridge.CastVote(signer, vote);
            }
            catch (Exception ex)
            {
                return ResultWrapper<bool>.Fail($"Unable to cast vote: {ex}", ErrorType.InternalError);
            }

            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<bool> clique_discard(Address signer)
        {
            try
            {
                _cliqueBridge.UncastVote(signer);
            }
            catch (Exception)
            {
                return ResultWrapper<bool>.Fail("Unable to uncast vote", ErrorType.InternalError);
            }

            return ResultWrapper<bool>.Success(true);
        }
    }
}