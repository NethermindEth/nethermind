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
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;

namespace Nethermind.Clique
{
    public class CliqueModule : ModuleBase, ICliqueModule
    {
        private readonly ICliqueBridge _cliqueBridge;

        public CliqueModule(IConfigProvider configurationProvider, ILogManager logManager, IJsonSerializer jsonSerializer, ICliqueBridge cliqueBridge) : base(configurationProvider, logManager, jsonSerializer)
        {
            _cliqueBridge = cliqueBridge ?? throw new ArgumentNullException(nameof(cliqueBridge));
        }

        public ModuleType ModuleType => ModuleType.Clique;

        public ResultWrapper<bool> clique_getSnapshot()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool> clique_getSnapshotAtHash(Data hash)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<Data[]> clique_getSigners()
        {
            return ResultWrapper<Data[]>.Success(_cliqueBridge.GetSigners().Select(s => new Data(s)).ToArray());
        }

        public ResultWrapper<Data[]> clique_getSignersAtHash(Data hash)
        {
            return ResultWrapper<Data[]>.Success(_cliqueBridge.GetSigners(new Keccak(hash.Value)).Select(s => new Data(s)).ToArray());
        }

        public ResultWrapper<bool> clique_propose(Data signer, bool vote)
        {
            try
            {
                _cliqueBridge.CastVote(new Address(signer.Value), vote);
            }
            catch (Exception)
            {
                return ResultWrapper<bool>.Fail("Unable to cast vote", ErrorType.InternalError);
            }

            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<bool> clique_discard(Data signer)
        {
            try
            {
                _cliqueBridge.UncastVote(new Address(signer.Value));
            }
            catch (Exception)
            {
                return ResultWrapper<bool>.Fail("Unable to uncast vote", ErrorType.InternalError);
            }

            return ResultWrapper<bool>.Success(true);
        }
    }
}