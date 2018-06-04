using System;

namespace Nethermind.Core
{
    public interface IPerfService
    {
        Guid StartPerfCalc();
        void EndPerfCalc(Guid id, string logMsg);
    }
}