// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
