# Builds Ruffle's headless "exporter" (renders SWF frames to PNG) from source, for Flash thumbnails.
# The Rust toolchain only exists inside this build stage; the worker image copies out the one binary.
# Build once (about 20-30 minutes) and tag it; worker.Dockerfile reads the tag:
#   docker build -f docker/ruffle-exporter.Dockerfile -t spankbank-ruffle-exporter:0.7.1 .
FROM rust:1-bookworm AS build
ARG RUFFLE_TAG=v0.7.1
RUN apt-get update \
    && apt-get install -y --no-install-recommends cmake pkg-config libasound2-dev libudev-dev \
    && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${RUFFLE_TAG} https://github.com/ruffle-rs/ruffle /ruffle
WORKDIR /ruffle
# Ruffle compiles its ActionScript 3 class stubs with Adobe's Java-based compiler during the build.
RUN apt-get update && apt-get install -y --no-install-recommends default-jre-headless && rm -rf /var/lib/apt/lists/*
RUN cargo build --release -p exporter

FROM scratch
COPY --from=build /ruffle/target/release/exporter /exporter
COPY --from=build /ruffle/LICENSE.md /LICENSE.md
