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


using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm
{
    public class TransactionTracer : ITransactionTracer
    {
        private readonly string _baseDir;
        private readonly IJsonSerializer _jsonSerializer;

        public TransactionTracer(string baseDir, IJsonSerializer jsonSerializer)
        {
            _baseDir = baseDir;
            _jsonSerializer = jsonSerializer;
        }

        public bool IsTracingEnabled => true;
        public void SaveTrace(Keccak transactionHash, TransactionTrace trace)
        {
            string path = Path.Combine(_baseDir, transactionHash.ToString(true));
            string text = _jsonSerializer.Serialize(trace, true);
            File.WriteAllText(path, text);
        }
    }
}