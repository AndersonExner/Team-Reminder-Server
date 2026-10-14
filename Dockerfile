FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/TeamReminder.Server/TeamReminder.Server.csproj src/TeamReminder.Server/
RUN dotnet restore src/TeamReminder.Server/TeamReminder.Server.csproj
COPY src/ src/
RUN dotnet publish src/TeamReminder.Server/TeamReminder.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY entrypoint.sh /app/entrypoint.sh
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "/app/entrypoint.sh"]
