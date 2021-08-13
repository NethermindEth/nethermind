//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Attributes;
using Nethermind.Crypto;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp))]
    public class InitCrypto : IStep
    {
        private readonly IBasicApi _api;

        public InitCrypto(INethermindApi api)
        {
            _api = api;
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public virtual Task Execute(CancellationToken _)
        {
            _api.EthereumEcdsa = new EthereumEcdsa(_api.SpecProvider!.ChainId, _api.LogManager);
            return Task.CompletedTask;
        }
    }
}
