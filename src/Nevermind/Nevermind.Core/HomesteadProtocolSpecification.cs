namespace Nevermind.Core
{
    public class HomesteadProtocolSpecification : IProtocolSpecification
    {
        public bool IsEip2Enabled => true;
        public bool IsEip7Enabled => true;
        public bool IsEip8Enabled => true;
        public bool IsEip150Enabled => false;
        public bool IsEip155Enabled => false;
        public bool IsEip158Enabled => false;
        public bool IsEip160Enabled => false;
        public bool IsEip170Enabled => false;
        public bool IsEmptyCodeContractBugFixed => true; // ???
        public bool IsEip186Enabled => false;
    }
}