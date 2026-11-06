# ChatBotRAGAPI: build with the .NET SDK, run on the smaller ASP.NET runtime image as a non-root user.
# Build from the repository root:  docker build -t chatbotragapi .
# global.json is not copied: it pins the SDK for development and tests, and would refuse an image's SDK band.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ChatBotRAGAPI/ChatBotRAGAPI.csproj ChatBotRAGAPI/
RUN dotnet restore ChatBotRAGAPI/ChatBotRAGAPI.csproj
COPY ChatBotRAGAPI/ ChatBotRAGAPI/
RUN dotnet publish ChatBotRAGAPI/ChatBotRAGAPI.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Documents, the job list, embeddings and uploads live in /app/data (Rag:DataDirectory, relative to the app):
# mount a volume there or they are lost with the container.
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
VOLUME /app/data
USER $APP_UID

# The image listens on 8080 (ASPNETCORE_HTTP_PORTS). Probe /health for liveness, /health/ready for readiness.
EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "ChatBotRAGAPI.dll"]
