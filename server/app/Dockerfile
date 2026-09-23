FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TnsApiImport.csproj ./
RUN dotnet restore TnsApiImport.csproj

COPY . ./
RUN dotnet publish TnsApiImport.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    TZ=UTC

EXPOSE 8080
EXPOSE 45001

COPY --from=build /app/publish ./

USER $APP_UID

ENTRYPOINT ["dotnet", "TnsApiImport.dll"]
