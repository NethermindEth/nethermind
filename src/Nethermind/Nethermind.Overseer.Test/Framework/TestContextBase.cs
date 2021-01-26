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
using Nethermind.Overseer.Test.Framework.Steps;
using Nethermind.Overseer.Test.JsonRpc;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Overseer.Test.Framework
{
    public abstract class TestContextBase<TContext, TState> : ITestContext where TState : ITestState where TContext : TestContextBase<TContext, TState>
    {
        protected TState State { get; }
        protected TestBuilder TestBuilder;

        protected TestContextBase(TState state)
        {
            State = state;
        }

        public TContext SwitchNode(string node)
        {
            TestBuilder.SwitchNode(node);
            return (TContext)this;
        }
        
        public TestBuilder LeaveContext()
        {
            return TestBuilder;
        }
        
        public TContext Wait(int delay = 5000, string name = "Wait")
            => Add(new WaitTestStep($"name {delay}", delay));

        protected TContext AddJsonRpc<TResult>(string name, string methodName,
            Func<Task<JsonRpcResponse<TResult>>> func, Func<TResult, bool> validator = null,
            Action<TState, JsonRpcResponse<TResult>> stateUpdater = null)
            => Add(new JsonRpcTestStep<TResult>(name,
                async () =>
                {
                    
                    var result = await ExecuteJsonRpcAsync(methodName, func);
                    if (result.IsValid)
                    {
                        stateUpdater?.Invoke(State, result);
                    }

                    return result;
                }, validator));

        protected TContext Add(TestStepBase step)
        {
            TestBuilder.QueueWork(step);
            return (TContext) this;
        }

        private async Task<JsonRpcResponse<TResult>> ExecuteJsonRpcAsync<TResult>(
            string methodName, Func<Task<JsonRpcResponse<TResult>>> func)
        {
            TestContext.WriteLine($"Sending JSON RPC call: '{methodName}'.");
            var delay = Task.Delay(20000);
            var funcTask = func();
            var first = await Task.WhenAny(delay, funcTask);
            if (first == delay)
            {
                string message = $"JSON RPC call '{methodName}' timed out";
                TestContext.WriteLine(message);
                throw new TimeoutException(message);
            }

            var result = await funcTask;

            TestContext.WriteLine($"Received a response for JSON RPC call '{methodName}'." +
                                   $"{Environment.NewLine}{JsonConvert.SerializeObject(result)}");

            return await funcTask;
        }

        public void SetBuilder(TestBuilder builder)
        {
            TestBuilder = builder;
        }
    }
}
