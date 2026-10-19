# aip-audit-service: .NET 8 Kafka consumer that keeps the audit trail of AIP decisions in MongoDB.
# The SDK stage cross-compiles for the target platform, so an Apple Silicon Mac can build linux/amd64 images.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/AipAuditService/AipAuditService.csproj src/AipAuditService/
RUN dotnet restore src/AipAuditService/AipAuditService.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/AipAuditService/AipAuditService.csproj -c Release -a $TARGETARCH -o /app --no-restore

# Chiseled (distroless) runtime: no shell or package manager, non-root
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AipAuditService.dll"]
