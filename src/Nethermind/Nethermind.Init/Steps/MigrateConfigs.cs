// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
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
            IReceiptConfig receiptConfig = _api.Config<IReceiptConfig>();

            MigrateInitConfig(miningConfig, receiptConfig);

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
            if (propertyInfos is null) return;

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

        private void MigrateInitConfig(IMiningConfig miningConfig, IReceiptConfig receiptConfig)
        {
            if (_api.Config<IInitConfig>().IsMining)
            {
                miningConfig.Enabled = true;
            }
            if (!_api.Config<IInitConfig>().StoreReceipts)
            {
                receiptConfig.StoreReceipts = false;
            }
            if (_api.Config<IInitConfig>().ReceiptsMigration)
            {
                receiptConfig.ReceiptsMigration = true;
            }
        }
    }
}
