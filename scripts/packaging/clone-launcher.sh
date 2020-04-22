echo Cloning Launcher
rm -rf nethermind.launcher
GIT_SSH_COMMAND='ssh -i ~/.ssh/id_rsa_nethermind' git clone git@github.com:NethermindEth/nethermind.launcher.git
