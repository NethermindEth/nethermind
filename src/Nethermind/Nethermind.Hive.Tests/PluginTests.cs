using Nethermind.Config.Test;
using NUnit.Framework;

namespace Nethermind.Hive.Tests
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class PluginTests
    {
        [Test]
        public void All_default_values_are_correct()
        {
            StandardConfigTests.ValidateDefaultValues();
        }

        [Test]
        public void All_config_items_have_descriptions_or_are_hidden()
        {
            StandardConfigTests.ValidateDescriptions();
        }
    }
}
