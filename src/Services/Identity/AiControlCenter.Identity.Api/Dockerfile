FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Services/Identity/AiControlCenter.Identity.Api/AiControlCenter.Identity.Api.csproj
RUN dotnet publish src/Services/Identity/AiControlCenter.Identity.Api/AiControlCenter.Identity.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
RUN mkdir -p /var/lib/aicontrolcenter/data-protection \
    && chown -R $APP_UID:$APP_UID /var/lib/aicontrolcenter/data-protection \
    && chmod 700 /var/lib/aicontrolcenter/data-protection
USER $APP_UID
ENTRYPOINT ["dotnet", "AiControlCenter.Identity.Api.dll"]
