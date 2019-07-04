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
        void SaveWord(ref UInt256 location, Span<byte> word);
        void SaveByte(ref UInt256 location, byte value);
        void Save(ref UInt256 location, Span<byte> value);
        void Save(ref UInt256 location, byte[] value);
        Span<byte> LoadSpan(ref UInt256 location);
        Span<byte> LoadSpan(ref UInt256 location, in UInt256 length);
        byte[] Load(ref UInt256 location, in UInt256 length);
        long CalculateMemoryCost(ref UInt256 location, in UInt256 length);
        List<string> GetTrace();
    }
}