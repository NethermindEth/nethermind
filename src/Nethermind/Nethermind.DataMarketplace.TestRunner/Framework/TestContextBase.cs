using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.DataMarketplace.TestRunner.JsonRpc;
using Nethermind.DataMarketplace.TestRunner.Tester;
using Nethermind.DataMarketplace.TestRunner.Tester.Steps;
using Newtonsoft.Json;

namespace Nethermind.DataMarketplace.TestRunner.Framework
{
    public abstract class TestContextBase<TContext, TState> : ITestContext where TState : ITestState where TContext : TestContextBase<TContext, TState>
    {
        protected ILogger<ITestContext> _logger;
        protected TestBuilder TestBuilder;
        protected TState _state;

        public TContext SetState(TState state)
        {
            _state = state;
            return (TContext)this;
        }
        
        public TContext Wait(int delay = 5000, string name = "Wait")
            => Add(new WaitTestStep(name, delay));

        protected TContext AddJsonRpc<TResult>(string name, string methodName,
            Func<Task<JsonRpcResponse<TResult>>> func, Func<TResult, bool> validator = null,
            Action<TState, JsonRpcResponse<TResult>> stateUpdater = null)
            => Add(new JsonRpcTestStep<TResult>(name,
                async () =>
                {
                    var result = await ExecuteJsonRpcAsync(methodName, func);
                    if (result.IsValid)
                    {
                        stateUpdater?.Invoke(_state, result);
                    }

                    return result;
                }, validator));

        protected TContext Add(TestStepBase step)
        {
            TestBuilder._steps.Add(step);
            return (TContext) this;
        }

        private async Task<JsonRpcResponse<TResult>> ExecuteJsonRpcAsync<TResult>(
            string methodName, Func<Task<JsonRpcResponse<TResult>>> func)
        {
            _logger.LogInformation($"Sending NDM JSON RPC call: '{methodName}'.");
            var delay = Task.Delay(20000);
            var funcTask = func();
            var first = await Task.WhenAny(delay, funcTask);
            if (first == delay)
            {
                string message = $"NDM JSON RPC call '{methodName}' timed out";
                _logger.LogInformation(message);
                throw new TimeoutException(message);
            }

            var result = await funcTask;

            _logger.LogInformation($"Received a response for NDM JSON RPC call '{methodName}'." +
                                   $"{Environment.NewLine}{JsonConvert.SerializeObject(result)}");

            return await funcTask;
        }

        public void SetBuilder(TestBuilder builder)
        {
            TestBuilder = builder;
        }
    }
}