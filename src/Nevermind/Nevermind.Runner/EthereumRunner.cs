using System;

namespace Nevermind.Runner
{
    public class EthereumRunner : IEthereumRunner
    {
        private readonly IJsonRpcRunner _jsonRpcRunner;

        public EthereumRunner(IJsonRpcRunner jsonRpcRunner)
        {
            _jsonRpcRunner = jsonRpcRunner;
        }

        public void Start()
        {
            throw new NotImplementedException();
        }

        public void Stop()
        {
            throw new NotImplementedException();
        }
    }
}