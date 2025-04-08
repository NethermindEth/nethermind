// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Config
{
    public class BlocksConfig : IBlocksConfig
    {
        public static bool AddVersionToExtraData { get; set; }

        public const string DefaultExtraData = "Nethermind";
        private byte[] _extraDataBytes = [];
        private string _extraDataString;

        public BlocksConfig()
        {
            _extraDataString = GetDefaultExtraData();
            // Validate that it doesn't overflow when converted to bytes
            ExtraData = _extraDataString;
        }

        private static string GetDefaultExtraData()
        {
            // Don't want block hashes in tests to change with every version
            if (!AddVersionToExtraData) return DefaultExtraData;

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

            return $"{DefaultExtraData} v{version[..index]}{alpha}";
        }

        public bool Enabled { get; set; }
        public long? TargetBlockGasLimit { get; set; } = null;

        public UInt256 MinGasPrice { get; set; } = 1.Wei();

        public bool RandomizedBlocks { get; set; }

        public ulong SecondsPerSlot { get; set; } = 12;

        public bool PreWarmStateOnBlockProcessing { get; set; } = true;
        public int PreWarmStateConcurrency { get; set; } = 0;

        public int BlockProductionTimeoutMs { get; set; } = 4_000;

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
    }
}
