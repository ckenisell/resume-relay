# Resume Relay in a container, for a NAS (Synology, uGreen, QNAP, Unraid, TrueNAS...) or any Docker host.
#
# The image wraps the very same program as the Linux download: the build context is the unpacked
# resume-relay-<version>-linux.tar.gz (its app/ folder), so "0.3.1" is the same program everywhere.
# The program is plain .NET (no native code), so one build runs on both amd64 and arm64 NAS models.
#
# Run it with HOST networking (network_mode: host). On Docker's default private network the
# "Scan network" button only sees Docker's own network, and Wake-on-LAN can't reach the players.
FROM mcr.microsoft.com/dotnet/aspnet:8.0

ARG VERSION=dev
LABEL org.opencontainers.image.title="Resume Relay" \
      org.opencontainers.image.description="Copies resume points between all the Zidoo players on your network." \
      org.opencontainers.image.source="https://github.com/ckenisell/resume-relay" \
      org.opencontainers.image.url="https://github.com/ckenisell/resume-relay" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}"

COPY app/ /app/

# Players, names, settings, Activity and posters live in /data: map it to a NAS folder so they
# survive updates. Change the port with RESUMERELAY_PORT if 8090 is taken on the NAS.
ENV RESUMERELAY_DATA=/data \
    RESUMERELAY_PORT=8090 \
    RESUMERELAY_DOCKER=1 \
    DOTNET_gcServer=0
VOLUME ["/data"]
EXPOSE 8090
WORKDIR /app

# Runs as root so it can always write to the mapped NAS folder, whatever that folder's owner is.
ENTRYPOINT ["dotnet", "/app/ResumeRelay.dll"]
