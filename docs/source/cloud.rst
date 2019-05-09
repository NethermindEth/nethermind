Cloud
*****

Digital Ocean
^^^^^^^^^^^^^

Create a new droplet

.. image:: clouds/do/1.png


Select Ubuntu version (18.10)

.. image:: clouds/do/2.png


Choose the minimal option

.. image:: clouds/do/3.png


Select preferred region (some will sync faster)

.. image:: clouds/do/4.png


Choose a hostname

.. image:: clouds/do/5.png


Wait for the droplet to be created

.. image:: clouds/do/6.png


Install packages::

  sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6 unzip

.. image:: clouds/do/7.png


Wait for the packages (input 'Y' when prompted)

.. image:: clouds/do/8.png


Download Nethermind Launcher::

  wget https://github.com/NethermindEth/nethermind/releases/download/0.9.9/nethermind-lin-x64.zip

.. image:: clouds/do/9.png


Unzip the package::

  unzip nethermind-lin-x64.zip -d nethermind && cd nethermind

.. image:: clouds/do/10.png
.. image:: clouds/do/11.png


Launch screen (and press ENTER)::

  screen

.. image:: clouds/do/12.png
.. image:: clouds/do/13.png

Launch Nethermind::

  ./Nethermind.Launcher

.. image:: clouds/do/14.png

Select the network

.. image:: clouds/do/16.png

Select the sync mode (fast sync or full archive)

.. image:: clouds/do/17.png


It can take some time (up to a few minutes before it finds the first nodes to sync with):

.. image:: clouds/do/18.png

