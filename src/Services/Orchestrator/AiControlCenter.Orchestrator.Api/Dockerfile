FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Services/Orchestrator/AiControlCenter.Orchestrator.Api/AiControlCenter.Orchestrator.Api.csproj
RUN dotnet publish src/Services/Orchestrator/AiControlCenter.Orchestrator.Api/AiControlCenter.Orchestrator.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "AiControlCenter.Orchestrator.Api.dll"]
