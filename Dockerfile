FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore needs the .razor files too. With only the .csproj, the SDK leaves out the
# Blazor web assets, and the published app has no _framework/blazor.web.js.
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# A new named volume copies this folder's owner, so the app's non-root user can write to it.
RUN mkdir /keys && chown "$APP_UID" /keys
USER $APP_UID

ENTRYPOINT []
CMD ["dotnet", "DataBridge.dll"]
