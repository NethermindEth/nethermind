systemd
*******

Please update (with PR) to be more specific.

::

    [Unit]
    Description=Nethermind Ethereum Daemon
    After=network.target

    [Service]
    User=nethermind
    Group=nethermind
    WorkingDirectory=/home/nethermind/nethermind
    ExecStart=/home/nethermind/nethermind/Nethermind.Runner --config goerli
    Environment=NETHERMIND_DETACHED_MODE=true
    Restart=on-failure

    KillSignal=SIGHUP

    [Install]
    WantedBy=default.target