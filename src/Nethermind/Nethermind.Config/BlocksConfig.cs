// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection.Metadata;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Config
{
    public class BlocksConfig : IBlocksConfig
    {
        public const string DefaultExtraData = "Nethermind";
        private byte[] _extraDataBytes = Encoding.UTF8.GetBytes(DefaultExtraData);
        private string _extraDataString = DefaultExtraData;

        public bool Enabled { get; set; }
        public long? TargetBlockGasLimit { get; set; } = null;

        public int? TargetBlobProductionLimit
        {
            get => _eip4844Config?.GetMaxBlobsPerBlock();
            set
            {
                if (TargetBlobProductionLimit != value)
                {
                    _eip4844Config = value is null || value == ConstantEip4844Config.Instance.GetMaxBlobsPerBlock()
                        ? null
                        : new CappedEip4844Config(value.Value);
                }
            }
        }

        public UInt256 MinGasPrice { get; set; } = 1.Wei();

        public bool RandomizedBlocks { get; set; }

        public ulong SecondsPerSlot { get; set; } = 12;

        public bool PreWarmStateOnBlockProcessing { get; set; } = true;

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

        private IEip4844Config _eip4844Config = null;
        public IEip4844Config GetEip4844Config() => _eip4844Config ?? ConstantEip4844Config.Instance;
    }
}
