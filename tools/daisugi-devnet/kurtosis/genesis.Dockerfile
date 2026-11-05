FROM ethpandaops/ethereum-genesis-generator@sha256:287f596b9a4ee72ae2aff194682f854650a8861542e489259adf0975cc0c9a54
COPY --from=ethpandaops/ethereum-genesis-generator@sha256:e05851fc126321196ab9fd79d0c8128dff2b78ef4b8440ca6321ffb090f53cb8 /apps/validator-mapping /apps/validator-mapping
COPY daisugi-genesis.sh /work/daisugi-genesis.sh
RUN mv /work/entrypoint.sh /work/upstream-entrypoint.sh && mv /work/daisugi-genesis.sh /work/entrypoint.sh && chmod +x /work/entrypoint.sh
