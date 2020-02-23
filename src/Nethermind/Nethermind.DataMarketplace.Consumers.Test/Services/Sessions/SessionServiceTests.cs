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

using FluentAssertions;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.DataAssets.Services;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Providers.Repositories;
using Nethermind.DataMarketplace.Consumers.Providers.Services;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Consumers.Sessions.Services;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Sessions
{
    [TestFixture]
    public class SessionServiceTests
    {
        private SessionService _sessionService;

        [SetUp]
        public void Setup()
        {
            DepositsInMemoryDb db = new DepositsInMemoryDb();
            IConsumerNotifier notifier = new ConsumerNotifier(Substitute.For<INdmNotifier>());
            IProviderRepository providerRepository = new ProviderInMemoryRepository(db);
            IProviderService providerService = new ProviderService(providerRepository, notifier, LimboLogs.Instance);
            
            IDepositDetailsRepository depositDetailsRepository = new DepositDetailsInMemoryRepository(db);
            IConsumerSessionRepository sessionRepository = new ConsumerSessionInMemoryRepository();
            
            DepositProvider depositProvider = new DepositProvider(depositDetailsRepository, new DepositUnitsCalculator(sessionRepository, Timestamper.Default), LimboLogs.Instance);
            DataAssetService dataAssetService = new DataAssetService(providerRepository, notifier, LimboLogs.Instance);
            
            _sessionService = new SessionService(providerService, depositProvider, dataAssetService, sessionRepository, Timestamper.Default, notifier, LimboLogs.Instance);
        }
        
        [Test]
        public void When_no_sessions_returns_empty_list()
        {
            var result = _sessionService.GetAllActive();
            result.Should().HaveCount(0);
        }
    }
}