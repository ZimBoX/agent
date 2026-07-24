# syntax=docker/dockerfile:1
# Сборка бэкенда ClaudeAssistant.Api в контейнер. Контекст сборки — корень репозитория
# (см. docker-compose.yml: context: ..), поэтому пути COPY начинаются с backend/.

# --- build ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Сначала только csproj — чтобы слой restore кэшировался и не пересобирался при
# каждом изменении исходников.
COPY backend/ClaudeAssistant.Api/ClaudeAssistant.Api.csproj backend/ClaudeAssistant.Api/
RUN dotnet restore backend/ClaudeAssistant.Api/ClaudeAssistant.Api.csproj

COPY backend/ClaudeAssistant.Api/ backend/ClaudeAssistant.Api/
RUN dotnet publish backend/ClaudeAssistant.Api/ClaudeAssistant.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# --- runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# PDFtoImage (рендер сканов PDF в картинки при извлечении текста) тянет SkiaSharp +
# PDFium, которым на Linux нужен fontconfig — без него падает при первой обработке PDF.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Слушать на всех интерфейсах контейнера (наружу пробрасывается портом в compose).
ENV ASPNETCORE_URLS=http://0.0.0.0:5149
EXPOSE 5149

ENTRYPOINT ["dotnet", "ClaudeAssistant.Api.dll"]
