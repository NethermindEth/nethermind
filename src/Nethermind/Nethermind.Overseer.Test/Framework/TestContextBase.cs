// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            return (TContext)this;
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
