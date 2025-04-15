// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Config
{
    public class BlocksConfig : IBlocksConfig
    {
        public const int DefaultMaxTxKilobytes = 9728;
        private const string _clientExtraData = "Nethermind";
        public static string DefaultExtraData = _clientExtraData;

        public static void SetDefaultExtraDataWithVersion() => DefaultExtraData = GetDefaultVersionExtraData();

        private byte[] _extraDataBytes = Encoding.UTF8.GetBytes(DefaultExtraData);
        private string _extraDataString = DefaultExtraData;

        private static string GetDefaultVersionExtraData()
        {
            ReadOnlySpan<char> version = ProductInfo.Version.AsSpan();
            int index = version.IndexOfAny('+', '-');
            string alpha = "";
            if (index >= 0)
            {
                if (version[index] == '-')
                {
                    alpha = "a";
                }
            }
            else
            {
                index = version.Length;
            }

            // Don't include too much if the version is long (can be in custom builds)
            index = Math.Min(index, 9);
            string defaultExtraData = $"{_clientExtraData} v{version[..index]}{alpha}";
            return defaultExtraData;
        }

        public bool Enabled { get; set; }
        public long? TargetBlockGasLimit { get; set; } = null;

        public UInt256 MinGasPrice { get; set; } = 1.Wei();

        public bool RandomizedBlocks { get; set; }

        public ulong SecondsPerSlot { get; set; } = 12;

        public bool PreWarmStateOnBlockProcessing { get; set; } = true;
        public int PreWarmStateConcurrency { get; set; } = 0;

        public int BlockProductionTimeoutMs { get; set; } = 4_000;
        public double SingleBlockImprovementOfSlot { get; set; } = 0.25;

        public int GenesisTimeoutMs { get; set; } = 40_000;

        public string ExtraData
        {
            get
            {
                return _extraDataString;
            }
            set
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                if (bytes is not null && bytes.Length > 32)
                {
                    throw new InvalidConfigurationException($"Extra Data length was more than 32 bytes. You provided: {_extraDataString}",
                        ExitCodes.TooLongExtraData);

                }

                _extraDataString = value;
                _extraDataBytes = bytes;
            }
        }
        public byte[] GetExtraDataBytes()
        {
            return _extraDataBytes;
        }

        public string GasToken { get => GasTokenTicker; set => GasTokenTicker = value; }

        public static string GasTokenTicker { get; set; } = "ETH";

        public long BlockProductionMaxTxKilobytes { get; set; } = DefaultMaxTxKilobytes;
    }
}
