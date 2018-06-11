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
using System.Numerics;

namespace Nethermind.Evm
{
    public interface IEvmMemory : IDisposable
    {
        long Size { get; }
        void SaveWord(BigInteger location, Span<byte> word);
        void SaveWord(BigInteger location, byte[] word);
        void SaveByte(BigInteger location, byte value);
        void SaveByte(BigInteger location, byte[] value);
        void Save(BigInteger location, Span<byte> value);
        void Save(BigInteger location, byte[] value);
        Span<byte> LoadSpan(BigInteger location);
        Span<byte> LoadSpan(BigInteger location, BigInteger length);
        byte[] Load(BigInteger location);
        byte[] Load(BigInteger location, BigInteger length);
        long CalculateMemoryCost(BigInteger position, BigInteger length);
        List<string> GetTrace();
    }
}