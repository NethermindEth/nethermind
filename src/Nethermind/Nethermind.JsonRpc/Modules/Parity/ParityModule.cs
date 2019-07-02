using System.Linq;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityModule : ModuleBase, IParityModule
    {
        private readonly IEcdsa _ecdsa;
        private readonly ITxPool _txPool;
        public override ModuleType ModuleType { get; } = ModuleType.Parity;

        public ParityModule(IEcdsa ecdsa, ITxPool txPool, ILogManager logManager)
            : base(logManager)
        {
            _ecdsa = ecdsa;
            _txPool = txPool;
        }

        public ResultWrapper<ParityTransaction[]> parity_pendingTransactions()
            => ResultWrapper<ParityTransaction[]>.Success(_txPool.GetPendingTransactions()
                .Select(t => new ParityTransaction(t, Rlp.Encode(t).Bytes,
                    t.IsSigned ? _ecdsa.RecoverPublicKey(t.Signature, t.Hash) : null)).ToArray());
    }
}