#!/bin/bash
docker run -p 8345:8345 -v ${PWD}/configs/mainnet.cfg:/app/configs/mainnet.cfg -it nethermind.runner
