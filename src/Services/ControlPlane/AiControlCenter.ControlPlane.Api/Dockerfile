FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Services/ControlPlane/AiControlCenter.ControlPlane.Api/AiControlCenter.ControlPlane.Api.csproj
RUN dotnet publish src/Services/ControlPlane/AiControlCenter.ControlPlane.Api/AiControlCenter.ControlPlane.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
EXPOSE 8080 8081
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "AiControlCenter.ControlPlane.Api.dll"]
