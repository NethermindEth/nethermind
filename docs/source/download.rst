Download
********

You can download the latest packages for all supported platforms from `here <http://downloads.nethermind.io>`_.

Windows
^^^^^^^

 * download Windows package
 * unzip
 * open the folder
 * run Nethermind.Launcher.exe

Linux (Ubuntu 16.04 or above)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

You have to replace ``[LINUX_PACKAGE_URL]`` and ``[LINUX_PACKAGE_FILENAME]`` with the actual values.

**Ubuntu 19.04 has an unresolved compatibility issue!**

Install dependencies
--------------------

::

 sudo apt update && sudo apt install libsnappy-dev libc6-dev libc6 unzip

Create a new user
-----------------

::

 sudo useradd -m -s /bin/bash nethermind

increase the maximum number of open files

::

 sudo bash -c 'echo "nethermind soft nofile 1000000" > /etc/security/limits.d/nethermind.conf'
 sudo bash -c 'echo "nethermind hard nofile 1000000" >> /etc/security/limits.d/nethermind.conf'

switch to the new user

::

 sudo su - nethermind

Get Nethermind
-------------------

download the latest Nethermind (not NDM) package (list is here: http://downloads.nethermind.io/)

::

 wget [LINUX_PACKAGE_URL]

extract the files

::

 unzip [LINUX_PACKAGE_FILENAME] -d nethermind

remove the no more need package **(optional)**

::

 rm [LINUX_PACKAGE_FILENAME]

switch directory

::

 cd nethermind

start the Nethermind launcher

::

 ./Nethermind.Launcher

to exit Nethermind press ``ctrl + c``

Manage Nethermind with systemd (optional)
-----------------------------------------

exit the session as user ``nethermind`` if still active

::

 exit

download the nethermind.service config

::

 wget https://raw.githubusercontent.com/NethermindEth/nethermind/master/scripts/nethermind.service

move the config into the systemd folder

::

 sudo mv nethermind.service /etc/systemd/system/


reload the systemd configs

::

 sudo systemctl daemon-reload

you can now ``start``, ``stop``, ``restart`` Nethermind with systemd

::

 sudo service nethermind start

display the current Nethermind output **(optional)**

::

 journalctl -u nethermind -f

enable autorun **(optional)**

::

 sudo systemctl enable nethermind

MacOS (10.12+)
^^^^^^^^^^^^^^

 * brew install gmp && brew install snappy && brew install lz4
 * download MacOS package
 * unzip
 * cd into the folder
 * ./Nethermind.Launcher
