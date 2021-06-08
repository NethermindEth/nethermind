using System;

namespace Nethermind.Consensus.Test
{
    public class TargetAdjustedGasLimitCalculatorTests
    {
        [Test]
        public void Is_case_insensitive()
        {
            EnvConfigSource configSource = new EnvConfigSource();
            Environment.SetEnvironmentVariable("NETHERMIND_A_A", "12", EnvironmentVariableTarget.Process);
            Assert.IsTrue(configSource.GetValue(typeof(int), "a", "A").IsSet);
        }
    }
}
