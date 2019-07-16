using System;
using FluentAssertions;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Initializers
{
    public class NdmInitializerFactoryTests
    {
        private Type _initializerType;
        private INdmModule _module;
        private INdmConsumersModule _consumersModule;
        private INdmInitializerFactory _factory;

        [SetUp]
        public void Setup()
        {
            _initializerType = null;
            _module = Substitute.For<INdmModule>();
            _consumersModule = Substitute.For<INdmConsumersModule>();
            _factory = new NdmInitializerFactory(_initializerType, _module, _consumersModule, LimboLogs.Instance);
        }

        [Test]
        public void should_throw_an_exception_when_type_is_null()
        {
            Action action = () => _factory.CreateOrFail();
            action.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void should_throw_an_exception_when_type_is_not_valid()
        {
            _initializerType = typeof(object);
            _factory = new NdmInitializerFactory(_initializerType, _module, _consumersModule, LimboLogs.Instance);
            Action action = () => _factory.CreateOrFail();
            action.Should().Throw<MissingMethodException>();
        }
        
        [Test]
        public void should_throw_an_exception_when_type_does_not_implement_ndm_initializer_interface()
        {
            _initializerType = typeof(FakeInitializer);
            _factory = new NdmInitializerFactory(_initializerType, _module, _consumersModule, LimboLogs.Instance);
            Action action = () => _factory.CreateOrFail();
            action.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void should_create_initializer_for_valid_type()
        {
            _initializerType = typeof(NdmInitializer);
            _factory = new NdmInitializerFactory(_initializerType, _module, _consumersModule, LimboLogs.Instance);
            var initializer = _factory.CreateOrFail();
            initializer.Should().NotBeNull();
            initializer.Should().BeOfType<NdmInitializer>();
        }


        private class FakeInitializer
        {
            public FakeInitializer(INdmModule ndmModule, INdmConsumersModule consumersModule)
            {
            }
        }
    }
}