// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection.Metadata;
using System.Text;
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

        public UInt256 MinGasPrice { get; set; } = 1.Wei();

        public bool RandomizedBlocks { get; set; }

        public ulong SecondsPerSlot { get; set; } = 12;


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
    }
}
