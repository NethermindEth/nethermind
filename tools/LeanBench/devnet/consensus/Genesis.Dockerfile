FROM ethpandaops/ethereum-genesis-generator:6.2.1
RUN mv /work/entrypoint.sh /work/entrypoint-upstream.sh
COPY genesis-entrypoint.sh /work/entrypoint.sh
RUN chmod +x /work/entrypoint.sh
