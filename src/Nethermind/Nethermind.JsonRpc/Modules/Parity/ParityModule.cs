using System.Linq;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityModule : ModuleBase, IParityModule
    {
        private readonly IEcdsa _ecdsa;
        private readonly IRlpDecoder<Transaction> _rlpDecoder;
        private readonly ITxPool _txPool;
        public override ModuleType ModuleType { get; } = ModuleType.Parity;

        public ParityModule(IEcdsa ecdsa, IRlpDecoder<Transaction> rlpDecoder, ITxPool txPool, ILogManager logManager)
            : base(logManager)
        {
            _ecdsa = ecdsa;
            _rlpDecoder = rlpDecoder;
            _txPool = txPool;
        }

        public ResultWrapper<ParityTransaction[]> parity_pendingTransactions()
            => ResultWrapper<ParityTransaction[]>.Success(_txPool.GetPendingTransactions()
                .Select(t => new ParityTransaction(t, _rlpDecoder.Encode(t).Bytes,
                    t.IsSigned ? _ecdsa.RecoverPublicKey(t.Signature, t.Hash) : null)).ToArray());
    }
}