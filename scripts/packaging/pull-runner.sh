echo Pulling Runner
cd nethermind && GIT_SSH_COMMAND='ssh -i ~/.ssh/id_rsa_nethermind' git pull && git submodule update --init
