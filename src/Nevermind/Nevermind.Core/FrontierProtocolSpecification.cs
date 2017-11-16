namespace Nevermind.Core
{
    public class FrontierProtocolSpecification : IProtocolSpecification
    {
        public bool IsEip2Enabled => false;
        public bool IsEip7Enabled => false;
        public bool IsEip8Enabled => false;
        public bool IsEip150Enabled => false;
        public bool IsEip155Enabled => false;
        public bool IsEip158Enabled => false;
        public bool IsEip160Enabled => false;
        public bool IsEip170Enabled => false;
        public bool IsEmptyCodeContractBugFixed => false; // ???
        public bool IsEip186Enabled => false;
    }
}