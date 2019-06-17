using System;
using Nethermind.Store;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks
{
    public interface IConsumerDbProvider : IDisposable
    {
        IDb ConsumerDepositApprovalsDb { get; }
        IDb ConsumerSessionsDb { get; }
        IDb ConsumerReceiptsDb { get; }
        IDb DepositsDb { get; }
    }
}