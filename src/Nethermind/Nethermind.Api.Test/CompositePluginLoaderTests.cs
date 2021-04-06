using System.Linq;
using FluentAssertions;
using Nethermind.Api.Extensions;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Api.Test
{
    public class CompositePluginLoaderTests
    {
        [Test]
        public void Can_load_none()
        {
            CompositePluginLoader compositePluginLoader = new();
            compositePluginLoader.Load(LimboLogs.Instance);
        }
        
        [Test]
        public void After_loading_none_returns_none()
        {
            CompositePluginLoader compositePluginLoader = new();
            compositePluginLoader.Load(LimboLogs.Instance);
            compositePluginLoader.PluginTypes.Should().BeEmpty();
        }
        
        [Test]
        public void Can_load_two()
        {
            CompositePluginLoader compositePluginLoader = new(
                SinglePluginLoader<TestPlugin>.Instance,
                SinglePluginLoader<TestPlugin2>.Instance);
            
            compositePluginLoader.Load(LimboLogs.Instance);
            compositePluginLoader.PluginTypes.Should().HaveCount(2);
            compositePluginLoader.PluginTypes.FirstOrDefault().Should().Be(typeof(TestPlugin));
            compositePluginLoader.PluginTypes.LastOrDefault().Should().Be(typeof(TestPlugin2));
        }
        
        [Test]
        public void Handles_duplicates_well()
        {
            CompositePluginLoader compositePluginLoader = new(
                SinglePluginLoader<TestPlugin>.Instance,
                SinglePluginLoader<TestPlugin>.Instance);
            
            compositePluginLoader.Load(LimboLogs.Instance);
            compositePluginLoader.PluginTypes.Should().HaveCount(1);
        }
    }
}
