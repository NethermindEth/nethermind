/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Nethermind.DataMarketplace.TestRunner.Framework;
using Nethermind.DataMarketplace.TestRunner.JsonRpc.Dto;
using Nethermind.DataMarketplace.TestRunner.Tester.Steps;

namespace Nethermind.DataMarketplace.TestRunner.Tester.Scenarios
{
    public class DefaultTestScenario : ITestScenario
    {
        private readonly ILogger<ITestScenario> _logger;
        public string Name => "Default test scenario";
        public IEnumerable<TestStepBase> Steps { get; }

        public DefaultTestScenario(TestBuilder testBuilder, NdmContext ndmContext, ILogger<ITestScenario> _logger)
        {
            this._logger = _logger;
            NdmState state = new NdmState();
            Steps = testBuilder.NewScenario()
                .SetContext(ndmContext)
                .StartDataProvider()
                .Wait()
                .SetState(state)
                .DP.DeployNdmContract()
                .DP.AddDataAsset(() => new DataAssetDto
                    {
                        Name = "Test data asset #1",
                        Description = "Test data asset #1 description",
                        UnitPrice = "0x1",
                        UnitType = "unit",
                        MinUnits = 1,
                        MaxUnits = 100000,
                        ExpiryRule = new DataAssetExpiryRuleDto
                        {
                            Value = "0x10000"
                        }
                    },
                    validator: d => (d?.Replace("0x", string.Empty).Length ?? 0) == 64,
                    stateUpdater: (s, r) => s.DataAssetId = r.Result)
                .DP.GetDataAssets(validator: d => d.Any())
                .StartDataConsumer()
                .Wait()
                .DC.GetDiscoveredDataAssets(validator: d => d.Any())
                .DC.MakeDeposit(() => new MakeDepositDto
                    {
                        DataAssetId = state.DataAssetId,
                        Units = 336,
                        Value = "33600000000000000000",
                        ExpiryTime = 1549531335
                    },
                    validator: d => (d?.Replace("0x", string.Empty).Length ?? 0) == 64,
                    stateUpdater: (s, r) =>
                    {
                        s.DepositId = r.Result;
                        _logger.LogInformation($"Setting state.DepositId = {s.DepositId}" );
                    })
                .Wait()
                .DC.SendDataRequest(() => state.DepositId, validator: d => !string.IsNullOrWhiteSpace(d))
                .DC.GetDeposits(validator: d =>
                {
                    var deposit = d.SingleOrDefault();
                    if (deposit == null)
                    {
                        return false;
                    }
                    
                    return deposit.TotalUnits == 0 &&
                           !deposit.StreamEnabled &&
                           !deposit.Subscriptions.Any();
                })
                .DC.EnableDataStream(() => state.DepositId, new[] {"test-sub"})
                .DP.SendData(() => new DataAssetDataDto
                {
                    DataAssetId = state.DataAssetId,
                    Data = "test-data",
                    Subscription = "test-sub"
                })
                .DC.PullData(() => state.DepositId, validator: d => d == "test-data")
                .DC.GetDeposits(validator: d =>
                {
                    var deposit = d.SingleOrDefault();
                    if (deposit == null)
                    {
                        return false;
                    }
                    
                    return deposit.TotalUnits == 1 && deposit.StartUnits == 0 &&
                           deposit.CurrentUnits == 1 && deposit.UnpaidUnits == 1 &&
                           deposit.StreamEnabled &&
                           deposit.Subscriptions.Contains("test-sub");
                })
                .DC.DisableDataStream(() => state.DepositId)
                .DP.SendData(() => new DataAssetDataDto
                {
                    DataAssetId = state.DataAssetId,
                    Data = "test-data",
                    Subscription = "test-sub"
                })
                .DC.PullData(() => state.DepositId, validator: string.IsNullOrWhiteSpace)
                .DC.EnableDataStream(() => state.DepositId, new[] {"test-sub"})
                .DP.SendData(() => new DataAssetDataDto
                {
                    DataAssetId = state.DataAssetId,
                    Data = "test-data-2",
                    Subscription = "test-sub"
                })
                .DC.PullData(() => state.DepositId, validator: d => d == "test-data-2")
                .DC.GetDeposits(validator: d =>
                {
                    var dataRequest = d.SingleOrDefault();
                    if (dataRequest == null)
                    {
                        return false;
                    }
                    
                    return dataRequest.TotalUnits == 2 && dataRequest.StartUnits == 0 &&
                           dataRequest.CurrentUnits == 2 && dataRequest.UnpaidUnits == 2 &&
                           dataRequest.StreamEnabled &&
                           dataRequest.Subscriptions.Contains("test-sub");
                })
                .KillDataConsumer()
                .StartDataConsumer()
                .DC.GetDeposits(validator: d =>
                {
                    var dataRequest = d.SingleOrDefault();
                    if (dataRequest == null)
                    {
                        return false;
                    }
                    
                    return dataRequest.TotalUnits == 2 && dataRequest.StartUnits == 0 &&
                           dataRequest.CurrentUnits == 0 && dataRequest.UnpaidUnits == 2 &&
                           dataRequest.StreamEnabled &&
                           dataRequest.Subscriptions.Contains("test-sub");
                })
                .DC.SendDataRequest(() => state.DepositId, validator: d => !string.IsNullOrWhiteSpace(d))
                .DC.EnableDataStream(() => state.DepositId, new[] {"test-sub"})
                .DP.SendData(() => new DataAssetDataDto
                {
                    DataAssetId = state.DataAssetId,
                    Data = "test-data-3",
                    Subscription = "test-sub"
                })
                .DP.SendData(() => new DataAssetDataDto
                {
                    DataAssetId = state.DataAssetId,
                    Data = "test-data-4",
                    Subscription = "test-sub"
                })
                .DP.SendData(() => new DataAssetDataDto
                {
                    DataAssetId = state.DataAssetId,
                    Data = "test-data-5",
                    Subscription = "test-sub"
                })
                .DC.PullData(() => state.DepositId, validator: d => d == "test-data-3")
                .DC.PullData(() => state.DepositId, validator: d => d == "test-data-4")
                .DC.PullData(() => state.DepositId, validator: d => d == "test-data-5")
                .DC.GetDeposits(validator: d =>
                {
                    var dataRequest = d.SingleOrDefault();
                    if (dataRequest == null)
                    {
                        return false;
                    }

                    return dataRequest.TotalUnits == 5 && dataRequest.StartUnits == 2 &&
                           dataRequest.CurrentUnits == 3 && dataRequest.UnpaidUnits == 3 &&
                           dataRequest.StreamEnabled &&
                           dataRequest.Subscriptions.Contains("test-sub");
                })
                .KillDataProvider()
                .KillDataConsumer()
                .LeaveContext()
                .Build();
        }
    }
}