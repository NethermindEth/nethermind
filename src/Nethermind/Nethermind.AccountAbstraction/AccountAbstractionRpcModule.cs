// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionRpcModule : IAccountAbstractionRpcModule
    {
        private readonly IDictionary<Address, IUserOperationPool> _userOperationPool;
        private readonly Address[] _supportedEntryPoints;

        static AccountAbstractionRpcModule()
        {
            Rlp.RegisterDecoders(typeof(UserOperationDecoder).Assembly);
        }

        public AccountAbstractionRpcModule(IDictionary<Address, IUserOperationPool> userOperationPool, Address[] supportedEntryPoints)
        {
            _userOperationPool = userOperationPool;
            _supportedEntryPoints = supportedEntryPoints;
        }

        public ResultWrapper<Keccak> eth_sendUserOperation(UserOperationRpc userOperationRpc, Address entryPointAddress)
        {
            if (!_supportedEntryPoints.Contains(entryPointAddress))
            {
                return ResultWrapper<Keccak>.Fail($"entryPoint {entryPointAddress} not supported, supported entryPoints: {string.Join(", ", _supportedEntryPoints.ToList())}");
            }

            // check if any entrypoint has both the sender and same nonce, if they do then the fee must increase 
            bool allow = _supportedEntryPoints
                .Select(ep => _userOperationPool[ep])
                .Where(pool => pool.IncludesUserOperationWithSenderAndNonce(userOperationRpc.Sender, userOperationRpc.Nonce))
                .All(pool => pool.CanInsert(new UserOperation(userOperationRpc)));

            if (!allow)
            {
                return ResultWrapper<Keccak>.Fail("op with same nonce and sender already present in a pool but op fee increase is not large enough to replace it");
            }

            return _userOperationPool[entryPointAddress].AddUserOperation(new UserOperation(userOperationRpc));
        }

        public ResultWrapper<Address[]> eth_supportedEntryPoints()
        {
            return ResultWrapper<Address[]>.Success(_supportedEntryPoints);
        }
    }
}
