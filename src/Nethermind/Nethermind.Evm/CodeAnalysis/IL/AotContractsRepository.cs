using Nethermind.Core.Crypto;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL
{
    public class AotContractsRepository
    {
        private static ConcurrentDictionary<ValueHash256, ILExecutionStep?> _processed = new();

        public static int WhiteListCount;

        public static void AddIledCode(ValueHash256 codeHash, ILExecutionStep? ilCode)
        {
            if (ilCode is null)
            {
                return;
            }
            _processed[codeHash] = ilCode;
        }

        public static bool TryGetIledCode(ValueHash256 codeHash, out ILExecutionStep ilCode)
        {
            if (_processed.TryGetValue(codeHash, out ilCode))
            {
                return ilCode is not null;
            }
            else
            {
                ilCode = null;
                return false;
            }
        }

        public static void ReserveForWhitelisting(ValueHash256 codeHash)
        {
            // Reserve the code hash for whitelisting by setting it to null
            _processed[codeHash] = null;
            Interlocked.Increment(ref WhiteListCount);
        }

        public static bool IsWhitelisted(ValueHash256 codeHash)
        {
            return Volatile.Read(ref WhiteListCount) > 0 && _processed.TryGetValue(codeHash, out ILExecutionStep? ilCode) && ilCode is null;
        }

        public static void ClearCache() => _processed.Clear();
    }
}
