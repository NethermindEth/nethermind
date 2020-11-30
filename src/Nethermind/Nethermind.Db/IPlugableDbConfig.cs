using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Config;

namespace Nethermind.Db
{
    public interface IPluggableDbConfig
    {
        ulong WriteBufferSize { get; set; }
        uint WriteBufferNumber { get; set; }
        ulong BlockCacheSize { get; set; }
        bool CacheIndexAndFilterBlocks { get; set; }

        uint RecycleLogFileNum { get; set; }
        bool WriteAheadLogSync { get; set; }
    }
}
