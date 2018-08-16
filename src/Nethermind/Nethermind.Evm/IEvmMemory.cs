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

using System;
using System.Collections.Generic;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm
{
    public interface IEvmMemory : IDisposable
    {
        ulong Size { get; }
        void SaveWord(UInt256 location, Span<byte> word);
        void SaveWord(UInt256 location, byte[] word);
        void SaveByte(UInt256 location, byte value);
        void SaveByte(UInt256 location, byte[] value);
        void Save(UInt256 location, Span<byte> value);
        void Save(UInt256 location, byte[] value);
        Span<byte> LoadSpan(UInt256 location);
        Span<byte> LoadSpan(UInt256 location, UInt256 length);
        byte[] Load(UInt256 location);
        byte[] Load(UInt256 location, UInt256 length);
        long CalculateMemoryCost(UInt256 location, UInt256 length);
        List<string> GetTrace();
    }
}