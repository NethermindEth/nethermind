using Nethermind.Core.Crypto;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL
{
    public class AotContractsRepository
    {
        private static ConcurrentDictionary<ValueHash256?, IPrecompiledContract> _processed = new();

        public static void AddIledCode(ValueHash256? codeHash, IPrecompiledContract ilCode)
        {
            if (codeHash is null || ilCode is null)
            {
                return;
            }

            _processed[codeHash] = ilCode;
        }

        public static bool TryGetIledCode(ValueHash256 codeHash, out IPrecompiledContract ilCode)
        {
            if (_processed.TryGetValue(codeHash, out ilCode))
            {
                return true;
            }
            else
            {
                ilCode = null;
                return false;
            }
        }

        public static void ClearCache() => _processed.Clear();
    }
}
