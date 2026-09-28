# Traffic generator image. Build context is tools/frame-tx-kurtosis-devnet.
#
#   docker build -f images/traffic/Dockerfile -t frame-tx-devnet/traffic:local .
#
# The vendored encoder is fetched during the build, pinned by commit and verified by checksum,
# so no third-party source is committed to this repository. The build needs network access for
# that step; images/build.sh drives it.

FROM python:3.12-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

RUN apt-get update \
  && apt-get install -y --no-install-recommends ca-certificates curl \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/frame-traffic

COPY traffic/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

COPY traffic/vendor/fetch.sh ./vendor/fetch.sh
ARG SOISPOKE_REF=main
ARG FRAME_TX_VENDOR_ALLOW_DRIFT=0
RUN SOISPOKE_REF="$SOISPOKE_REF" FRAME_TX_VENDOR_ALLOW_DRIFT="$FRAME_TX_VENDOR_ALLOW_DRIFT" \
  bash ./vendor/fetch.sh /opt/frame-traffic/vendor

COPY traffic/frame_traffic ./frame_traffic
# Shipped with the image so the shape checks can run against exactly the code and encoder the
# generator will use, with no Python dependencies on the host.
COPY traffic/tests ./tests

# Fails the build rather than the run if the encoder's envelope dialect drifted from the one
# both clients implement.
RUN python -c "from frame_traffic import shapes; shapes.load_encoder(); print('encoder dialect OK')"

VOLUME /results

ENTRYPOINT ["python", "-m", "frame_traffic"]
CMD ["--help"]
