FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
WORKDIR /app

# Cache NuGet packages independently of source code changes
COPY src/ConsoleApp/ConsoleApp.csproj src/ConsoleApp/
COPY src/Models/Models.csproj src/Models/
COPY src/Scrapers/Scrapers.csproj src/Scrapers/
COPY Scrapper.slnx .
RUN dotnet restore src/ConsoleApp/ConsoleApp.csproj

COPY . .
RUN dotnet publish src/ConsoleApp/ConsoleApp.csproj -c Release -o ./publish --no-restore

# Instalar Playwright usando el script del publish output
RUN dotnet tool install --global Microsoft.Playwright.CLI
ENV PATH="$PATH:/root/.dotnet/tools"
RUN ./publish/playwright.ps1 install chromium || \
    PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright dotnet run --project src/ConsoleApp/ConsoleApp.csproj -- install chromium || \
    playwright install chromium

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN apt-get update && apt-get install -y \
    libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
    libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 \
    libgbm1 libasound2t64 libpango-1.0-0 libcairo2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /app/publish .
COPY --from=builder /root/.cache/ms-playwright /root/.cache/ms-playwright

EXPOSE 3003
CMD ["dotnet", "horarioshub-scraper.dll"]