
#!/bin/bash
echo 'Installing Ubuntu Archive Tools'
git clone https://git.launchpad.net/ubuntu-archive-tools
sudo apt-get install ubuntu-dev-tools -y
cd ubuntu-archive-tools
echo 'Copying Packages'
python3 copy-package -y -b -p nethermindeth --ppa-name=nethermind -s jammy --to-suite=focal nethermind
python3 copy-package -y -b -p nethermindeth --ppa-name=nethermind -s jammy --to-suite=kinetic nethermind
python3 copy-package -y -b -p nethermindeth --ppa-name=nethermind -s jammy --to-suite=bionic nethermind
python3 copy-package -y -b -p nethermindeth --ppa-name=nethermind -s jammy --to-suite=trusty nethermind
cd ..
echo 'Cleanup'
sudo rm -rf ubuntu-archive-tools
