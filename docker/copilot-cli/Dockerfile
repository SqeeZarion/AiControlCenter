FROM debian:bookworm-slim AS download

ARG COPILOT_CLI_VERSION=1.0.103
ARG TARGETARCH
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl tar \
    && rm -rf /var/lib/apt/lists/* \
    && case "$TARGETARCH" in \
         amd64) platform="linux-x64" ;; \
         arm64) platform="linux-arm64" ;; \
         *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
       esac \
    && release="https://github.com/github/copilot-cli/releases/download/v${COPILOT_CLI_VERSION}" \
    && asset="copilot-${platform}.tar.gz" \
    && mkdir -p /opt/copilot \
    && curl --fail --show-error --silent --location "$release/SHA256SUMS.txt" -o /tmp/SHA256SUMS.txt \
    && curl --fail --show-error --silent --location "$release/$asset" -o "/tmp/$asset" \
    && grep -E "^[0-9a-fA-F]{64}[[:space:]]+\\*?${asset}$" /tmp/SHA256SUMS.txt \
       | sed "s#${asset}#/tmp/${asset}#" \
       | sha256sum --check --strict - \
    && tar -xzf "/tmp/$asset" -C /opt/copilot \
    && test -x /opt/copilot/copilot

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS manager-build
WORKDIR /src
COPY src/Services/Integrations/AiControlCenter.LocalCopilot.Sidecar/AiControlCenter.LocalCopilot.Sidecar.csproj ./
RUN dotnet restore AiControlCenter.LocalCopilot.Sidecar.csproj
COPY src/Services/Integrations/AiControlCenter.LocalCopilot.Sidecar/*.cs ./
RUN dotnet publish AiControlCenter.LocalCopilot.Sidecar.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/copilot /var/empty /tmp/aicontrolcenter-session \
    && chown -R 1654:1654 /var/lib/copilot /var/empty /tmp/aicontrolcenter-session
COPY --from=download --chown=1654:1654 /opt/copilot /opt/copilot
COPY --from=manager-build --chown=1654:1654 /app /app
RUN ln -s /opt/copilot/copilot /usr/local/bin/copilot
USER 1654:1654
WORKDIR /var/empty
ENV HOME=/var/lib/copilot \
    COPILOT_HOME=/var/lib/copilot \
    COPILOT_AUTO_UPDATE=false \
    COPILOT_PLUGIN_DIR_ONLY=true
ENTRYPOINT ["dotnet", "/app/AiControlCenter.LocalCopilot.Sidecar.dll"]
