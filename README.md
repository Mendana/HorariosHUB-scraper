# HorariosHUB Scraper

Scraper en .NET que obtiene los horarios oficiales de Ingeniería Informática y
Matemáticas (Universidad de Oviedo) para alimentar **HorariosHub**, el
sistema de gestión de horarios del programa PCEO (doble grado Informática +
Matemáticas).

Usa Playwright para navegar las webs/hojas oficiales de horarios y expone una
API mínima en ASP.NET Core para lanzar el scraping bajo demanda.

## Stack

- .NET 10 (ASP.NET Core minimal API)
- Playwright (Chromium headless)
- CsvHelper / ExcelDataReader para el parseo de horarios
- Docker

## Estructura

- `src/ConsoleApp` — API HTTP que expone el scraper
- `src/Scrapers` — lógica de scraping (Informática, Matemáticas, grupos)
- `src/Models` — modelos compartidos

## Endpoints

- `POST /scrape` — lanza el scraping de Informática y Matemáticas, devuelve CSV
- `POST /groups` — dado un UO (código de estudiante), devuelve sus grupos en CSV

## Desarrollo local

```bash
dotnet restore
dotnet run --project src/ConsoleApp
```

Escucha en `http://0.0.0.0:3003`.

## Docker

```bash
docker build -t horarioshub-scraper .
docker run -p 3003:3003 horarioshub-scraper
```

## Contribuir

Ver [CONTRIBUTING.md](CONTRIBUTING.md).
