//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain
{
    public static class BlockTraceDumper
    {
        public static void LogDiagnosticTrace(
            IBlockTracer blockTracer,
            Keccak blockHash,
            ILogger logger)
        {
            static FileStream GetFileStream(string name) =>
                new FileStream(
                    Path.Combine(Path.GetTempPath(), name),
                    FileMode.Create,
                    FileAccess.Write);

            GethLikeBlockTracer gethTracer = blockTracer as GethLikeBlockTracer;
            ParityLikeBlockTracer parityTracer = blockTracer as ParityLikeBlockTracer;
            if (gethTracer != null)
            {
                using FileStream diagnosticFile = GetFileStream($"gethStyle_{blockHash}.txt");
                EthereumJsonSerializer serializer = new EthereumJsonSerializer();
                IReadOnlyCollection<GethLikeTxTrace> trace = gethTracer.BuildResult();
                serializer.Serialize(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Geth-style trace of block {blockHash} in file {diagnosticFile.Name}");
            }

            if (parityTracer != null)
            {
                using FileStream diagnosticFile = GetFileStream($"parityStyle_{blockHash}.txt");
                EthereumJsonSerializer serializer = new EthereumJsonSerializer();
                IReadOnlyCollection<ParityLikeTxTrace> trace = parityTracer.BuildResult();
                serializer.Serialize(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Parity-style trace of block {blockHash} in file {diagnosticFile.Name}");
            }
        }
    }
}