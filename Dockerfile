# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Core.Gateway.Api/Core.Gateway.Api.csproj src/Core.Gateway.Api/
RUN dotnet restore src/Core.Gateway.Api/Core.Gateway.Api.csproj

COPY src/Core.Gateway.Api/ src/Core.Gateway.Api/
RUN dotnet publish src/Core.Gateway.Api/Core.Gateway.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish/ ./

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Core.Gateway.Api.dll"]
