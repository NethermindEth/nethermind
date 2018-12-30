using System.Collections.Generic;
using Nethermind.Stats;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class ArgsConfigProviderTests
    {
        [Test]
        public void Test()
        {
            Dictionary<string, string> args = new Dictionary<string, string>();
            ArgsConfigSource configSource = new ArgsConfigSource(args);

            IStatsConfig statsConfig = configSource.GetConfig<IStatsConfig>();
        }
    }
}