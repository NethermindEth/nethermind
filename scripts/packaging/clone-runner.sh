echo Cloning Runner
rm -rf nethermind
if [ "$1" != "" ]; then
    GIT_SSH_COMMAND='ssh -i ~/.ssh/id_rsa_nethermind' git clone --branch $1 git@github.com:NethermindEth/nethermind.git --recursive
else 
    GIT_SSH_COMMAND='ssh -i ~/.ssh/id_rsa_nethermind' git clone git@github.com:NethermindEth/nethermind.git --recursive
fi
