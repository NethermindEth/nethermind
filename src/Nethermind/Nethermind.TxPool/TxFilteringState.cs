using Nethermind.Core;

namespace Nethermind.TxPool;

public class TxFilteringState
{

    private readonly IAccountStateProvider _accounts;
    private readonly Transaction _tx;

    public TxFilteringState(Transaction tx, IAccountStateProvider accounts)
    {
        _accounts = accounts;
        _tx = tx;
    }

    private Account? _senderAccount = null;
    public Account SenderAccount { get { return _senderAccount ??= _accounts.GetAccount(_tx.SenderAddress!); } }
}
