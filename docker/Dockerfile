# syntax=docker/dockerfile:1
#
# Container image behind the `oci` package of the Official MCP Registry entry
# (io.github.Graphene-Lab/agentbridge) and behind `docker run` for anyone who prefers a
# container to the installers. See docs/MCP.md and docs-dev/RELEASING.md.
#
# The build context is the linux-x64 tree the release already publishes (the very
# agentbridge-linux-x64.tar.gz attached to the GitHub release), so the image ships exactly
# those bits - binaries, tool plugins and assets - and nothing is rebuilt here:
#
#   tar -xzf agentbridge-linux-x64.tar.gz -C publish
#   docker build -f docker/Dockerfile -t agentbridge publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble

# Passed by the release build (-build-arg VERSION=<registry semver>). Without it the label would
# inherit the Ubuntu version of the base image, which says nothing about AgentBridge.
ARG VERSION=

# The Official MCP Registry proves ownership of an `oci` package by reading this label from the
# image config and comparing it with `name` in server.json (registry
# internal/validators/registries/oci.go). It must stay identical to that name or every publish
# is rejected - the two are edited together (docs-dev/RELEASING.md).
LABEL io.modelcontextprotocol.server.name="io.github.Graphene-Lab/agentbridge" \
      org.opencontainers.image.title="AgentBridge" \
      org.opencontainers.image.description="Autonomous office agents with an MCP endpoint" \
      org.opencontainers.image.url="https://github.com/Graphene-Lab/AgentBridge" \
      org.opencontainers.image.source="https://github.com/Graphene-Lab/AgentBridge" \
      org.opencontainers.image.licenses="AGPL-3.0" \
      org.opencontainers.image.version="${VERSION}"

# The office tools render documents with system fonts; the runtime-deps base ships none and the
# output would come out as empty boxes.
RUN apt-get update \
 && apt-get install -y --no-install-recommends fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:5290 \
    DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
COPY . /app/

# Config, provider keys and sessions live under PersistentData next to the executable: without a
# volume every container starts from the seeded defaults and loses them on the next run.
VOLUME ["/app/PersistentData"]
EXPOSE 5290

# No curl in the runtime-deps image, so the probe is a plain TCP connect.
HEALTHCHECK --interval=30s --timeout=5s --start-period=300s --retries=3 \
  CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/5290 || exit 1"]

# --headless: there is no terminal to drive the TUI in a container.
# --no-update: the built-in self-updater swaps the binaries on disk; the image owns them.
ENTRYPOINT ["/app/agent", "--headless", "--no-update", "--Urls", "http://0.0.0.0:5290"]