using Nevermind.Core.Encoding;

namespace Nevermind.Store
{
    internal class KeccakOrRlp
    {
        public KeccakOrRlp(Keccak keccak)
        {
            _keccak = keccak;
        }

        public KeccakOrRlp(Rlp rlp)
        {
            if (rlp.Bytes.Length < 32)
            {
                _rlp = rlp;
            }
            else
            {
                _keccak = Keccak.Compute(rlp);
            }
        }

        private readonly Rlp _rlp;
        private readonly Keccak _keccak;

        public byte[] Bytes => _rlp?.Bytes ?? _keccak.Bytes;

        public bool IsKeccak => _keccak != null;

        public Keccak GetKeccakOrComputeFromRlp()
        {
            return _keccak ?? Keccak.Compute(_rlp);
        }

        public override string ToString()
        {
            if (IsKeccak)
            {
                string full = Hex.FromBytes(Bytes, IsKeccak);
                return IsKeccak ? full.Substring(0, 6) : full;
            }

            return PatriciaTree.RlpDecode(new Rlp(Bytes)).ToString();
        }
    }
}