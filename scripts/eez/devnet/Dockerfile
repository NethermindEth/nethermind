ARG BASE_IMAGE
FROM ${BASE_IMAGE}
ARG SKIP_CONSENSUS_LAYER_CALLS
ENV EEZ_SKIP_CONSENSUS_LAYER_CALLS=${SKIP_CONSENSUS_LAYER_CALLS}
COPY start-follower.sh /nethermind/start-follower.sh
ENTRYPOINT ["/bin/sh", "/nethermind/start-follower.sh"]
