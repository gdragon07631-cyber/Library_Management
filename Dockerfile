FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/ backend/
COPY frontend/ frontend/
RUN dotnet restore backend/Project.csproj
RUN dotnet publish backend/Project.csproj --configuration Release --no-restore --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app/backend
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/frontend/wwwroot/uploads /app/backend/Data/Keys \
    && chown -R "$APP_UID:$APP_UID" /app
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
COPY --from=build --chown=$APP_UID:$APP_UID /src/frontend/wwwroot/ /app/frontend/wwwroot/
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Project.dll"]
