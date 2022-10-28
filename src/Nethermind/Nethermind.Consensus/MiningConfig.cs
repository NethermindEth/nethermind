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

using System;
using System.Text;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Consensus
{
    public class MiningConfig : IMiningConfig
    {
        private byte[] _extraDataBytes = Encoding.UTF8.GetBytes("Nethermind");
        private string _extraDataString = "Nethermind";

        public bool Enabled { get; set; } = false;

        public long? TargetBlockGasLimit { get; set; } = null;

        public UInt256 MinGasPrice { get; set; } = 1.Wei();

        public bool RandomizedBlocks { get; set; }

        public string ExtraData
        {
            get
            {
                return _extraDataString;
            }
            set
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                if (bytes != null && bytes.Length > 32)
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
