using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    public class TraceModuleTests
    {
        [Test]
        public void Trace_replay_transaction()
        {   
            ITracer tracer = Substitute.For<ITracer>();
            ITraceModule module = new TraceModule(Substitute.For<IConfigProvider>(), NullLogManager.Instance, new UnforgivingJsonSerializer(), tracer);
            JsonRpcResponse response = RpcTest.TestRequest(module, "trace_replayTransaction", TestObject.KeccakC.ToString(true), "[\"call\"]");
            
            Assert.IsNull(response.Error, "error");
            Assert.IsNull(response.Result, "result");
        }
    }
}