// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
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

        [SetUp]
        public void Setup()
        {
            _initializerType = null;
            _module = Substitute.For<INdmModule>();
            _consumersModule = Substitute.For<INdmConsumersModule>();
        }

        [Test]
        public void should_throw_an_exception_when_type_is_not_valid()
        {
            _initializerType = typeof(object);
            NdmInitializerFactory factory = new NdmInitializerFactory(_initializerType, _module, _consumersModule, LimboLogs.Instance);
            Action action = () => factory.CreateOrFail();
            action.Should().Throw<MissingMethodException>();
        }

        [Test]
        public void should_throw_an_exception_when_type_does_not_implement_ndm_initializer_interface()
        {
            _initializerType = typeof(FakeInitializer);
            NdmInitializerFactory factory = new NdmInitializerFactory(_initializerType, _module, _consumersModule, LimboLogs.Instance);
            Action action = () => factory.CreateOrFail();
            action.Should().Throw<MissingMethodException>();
        }

        [Test]
        public void should_create_initializer_for_valid_type()
        {
            _initializerType = typeof(NdmInitializer);
            NdmInitializerFactory factory = new NdmInitializerFactory(_initializerType, _module, _consumersModule, LimboLogs.Instance);
            Action action = () => factory.CreateOrFail();
            factory.Should().NotBeNull();
            factory.CreateOrFail().Should().BeOfType<NdmInitializer>();
        }


        private class FakeInitializer
        {
            public FakeInitializer(INdmModule ndmModule, INdmConsumersModule consumersModule)
            {
            }
        }
    }
}
