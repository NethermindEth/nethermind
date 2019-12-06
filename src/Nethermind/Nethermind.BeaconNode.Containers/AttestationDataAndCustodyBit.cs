namespace Nethermind.BeaconNode.Containers
{
    public class AttestationDataAndCustodyBit
    {
        public AttestationDataAndCustodyBit(AttestationData data, bool custodyBit)
        {
            Data = data;
            CustodyBit = custodyBit;
        }

        /// <summary>Gets a challengable bit (SSZ-bool, 1 byte) for the custody of crosslink data</summary>
        public bool CustodyBit { get; }

        public AttestationData Data { get; }
    }
}
