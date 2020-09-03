using Ethereum.Test.Base;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test
{
    [TestFixture]
    public class StateTestTxTracerTest : VirtualMachineTestsBase
    {
        private StateTestTxTracer tracer;

        [SetUp]
        public void SetUp()
        {
            tracer = new StateTestTxTracer();
        }

        [Test]
        public void Does_not_throw_on_call()
        {
            byte[] code = Prepare.EvmCode
                .CallWithValue(TestItem.AddressC, 50000, 1000000.Ether())
                .Done;

            Assert.DoesNotThrow(() => Execute<StateTestTxTracer>(tracer, code));
        }
    }
}