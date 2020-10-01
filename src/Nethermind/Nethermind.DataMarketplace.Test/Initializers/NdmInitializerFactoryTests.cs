//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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