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

using System;
using System.Threading.Tasks;
using Nethermind.Overseer.Test.JsonRpc;

namespace Nethermind.Overseer.Test.Framework.Steps
{
    public class JsonRpcTestStep<T> : TestStepBase
    {
        private readonly Func<T, bool> _validator;
        private readonly Func<Task<JsonRpcResponse<T>>> _request;
        private JsonRpcResponse<T> _response;

        public JsonRpcTestStep(string name,
            Func<Task<JsonRpcResponse<T>>> request,
            Func<T, bool> validator) : base(name)
        {
            _validator = validator;
            _request = request;
        }

        public override async Task<TestResult> ExecuteAsync()
        {
            _response = await _request();

            return _response.IsValid
                ? GetResult(_validator?.Invoke(_response.Result) ?? true)
                : GetResult(false);
        }
    }
}
