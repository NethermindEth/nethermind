ARG LIGHTHOUSE_IMAGE=daisugi/lighthouse:eip8288-lean
FROM busybox:1.37-musl AS busybox
FROM ${LIGHTHOUSE_IMAGE}
COPY --from=busybox /bin/busybox /bin/sh
ENV PATH=/usr/local/bin:/usr/bin:/bin:/
ENTRYPOINT []
USER 0
