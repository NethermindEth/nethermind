using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    public class TraceModuleTests
    {
        [TestCase("[\"trace\"]")]
        [TestCase("[\"vmTrace\"]")]
        [TestCase("[\"stateDiff\"]")]
        [TestCase("[\"stateDiff\", \"stateDiff\", \"vmTrace\"]")]
        public void Trace_replay_transaction(string types)
        {   
            ParityTraceAction subtrace = new ParityTraceAction();
            subtrace.Value = 67890;
            subtrace.CallType = "call";
            subtrace.From = TestObject.AddressC;
            subtrace.To = TestObject.AddressD;
            subtrace.Input = Bytes.Empty;
            subtrace.Gas = 10000;
            subtrace.TraceAddress = new int[] {0, 0};

            ParityLikeTxTrace result = new ParityLikeTxTrace();
            result.Action = new ParityTraceAction();
            result.Action.Value = 12345;
            result.Action.CallType = "init";
            result.Action.From = TestObject.AddressA;
            result.Action.To = TestObject.AddressB;
            result.Action.Input = new byte[] {1, 2, 3, 4, 5, 6};
            result.Action.Gas = 40000;
            result.Action.TraceAddress = new int[] {0};
            result.Action.Subtraces.Add(subtrace);

            result.BlockHash = TestObject.KeccakB;
            result.BlockNumber = 123456;
            result.TransactionHash = TestObject.KeccakC;
            result.TransactionPosition = 5;
            result.Action.TraceAddress = new int[] {1, 2, 3};

            ParityAccountStateChange stateChange = new ParityAccountStateChange();
            stateChange.Balance = new ParityStateChange<UInt256>(1, 2);
            stateChange.Nonce = new ParityStateChange<UInt256>(0, 1);
            stateChange.Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            stateChange.Storage[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2});

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange>();
            result.StateChanges.Add(TestObject.AddressC, stateChange);
            
            ITracer tracer = Substitute.For<ITracer>();
            tracer.ParityTrace(TestObject.KeccakC, Arg.Any<ParityTraceTypes>()).Returns(result);
            
            ITraceModule module = new TraceModule(Substitute.For<IConfigProvider>(), NullLogManager.Instance, new UnforgivingJsonSerializer(), tracer);
            
            JsonRpcResponse response = RpcTest.TestRequest(module, "trace_replayTransaction", TestObject.KeccakC.ToString(true), types);
            
            Assert.IsNull(response.Error, "error");
            Assert.NotNull(response.Result, "result");
//            Assert.False(response.Result is string s && s.Contains("\""));
        }
    }
}