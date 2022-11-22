//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core.Exceptions;

namespace Nethermind.Init.Steps
{
    public class MigrateConfigs : IStep
    {
        private readonly INethermindApi _api;

        public MigrateConfigs(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            IMiningConfig miningConfig = _api.Config<IMiningConfig>();

            MigrateInitConfig(miningConfig);

            var blocksConfig = miningConfig.BlocksConfig;
            var value = _api.Config<IBlocksConfig>();
            MigrateBlocksConfig(blocksConfig, value);

            return Task.CompletedTask;
        }

        //This function is marked publick and static for use in tests
        public static void MigrateBlocksConfig(IBlocksConfig? blocksConfig, IBlocksConfig? value)
        {
            PropertyInfo[]? propertyInfos = blocksConfig?.GetType().GetInterface($"{nameof(IBlocksConfig)}")?.GetProperties();

            //Loop over config properties checking mismaches and changing defaults
            //So that on given and current inner configs we would only have same values
            if (propertyInfos == null) return;

            foreach (PropertyInfo? propertyInfo in propertyInfos)
            {
                ConfigItemAttribute? attribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();
                string expectedDefaultValue = attribute?.DefaultValue.Trim('"') ?? "";
                object? valA = propertyInfo.GetValue(blocksConfig);
                object? valB = propertyInfo.GetValue(value);

                string valAasStr = (valA?.ToString() ?? "null");
                string valBasStr = (valB?.ToString() ?? "null");

                if (valBasStr != valAasStr)
                {
                    if (valAasStr == expectedDefaultValue)
                    {
                        propertyInfo.SetValue(blocksConfig, valB);
                    }
                    else if (valBasStr == expectedDefaultValue)
                    {
                        propertyInfo.SetValue(value, valA);
                    }
                    else
                    {
                        throw new InvalidConfigurationException($"Configuration mismatch at {propertyInfo.Name} " +
                                                                $"with conflicting values {valA} and {valB}",
                            ExitCodes.ConflictingConfigurations);
                    }
                }
            }
        }

        private void MigrateInitConfig(IMiningConfig miningConfig)
        {
            if (_api.Config<IInitConfig>().IsMining)
            {
                miningConfig.Enabled = true;
            }
        }
    }
}
