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
# PDFtoImage (рендер PDF в картинки) тянет SkiaSharp + PDFium, которым на Linux нужен
# fontconfig — без него падает при первой обработке PDF. Плюс fonts-dejavu-core: наши
# подписи в монтаже листов (DrawingRenderService) рисуются SkiaSharp'ом и содержат
# кириллицу («Лист N») — без установленного шрифта с кириллицей они выйдут пустыми.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Слушать на всех интерфейсах контейнера (наружу пробрасывается портом в compose).
ENV ASPNETCORE_URLS=http://0.0.0.0:5149
EXPOSE 5149

ENTRYPOINT ["dotnet", "ClaudeAssistant.Api.dll"]
