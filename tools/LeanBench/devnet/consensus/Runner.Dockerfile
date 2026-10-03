FROM nethermind:gd8-current
COPY client/ /nethermind/
WORKDIR /nethermind
ENTRYPOINT ["/nethermind/nethermind"]
