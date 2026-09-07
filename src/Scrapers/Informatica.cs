using Microsoft.Playwright;
using Models;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;

namespace Scrapers;

public class Informatica
{
    private const int MaxRetries = 3;
    private const int ChunkSize = 100;

    private readonly string? _url;

    public Informatica()
    {
        _url = Environment.GetEnvironmentVariable("SCRAPER_INFORMATICA_URL");
    }

    public async Task<List<ScheduleClass>> DownloadSchedulesAsync(
        string outputFolder = "dataI")
    {
        var downloadDir = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(downloadDir);

        Console.WriteLine($"[DIR] Working dir: {Directory.GetCurrentDirectory()}");
        Console.WriteLine($"[DIR] dataI path: {downloadDir}");

        var results = new List<ScheduleClass>();

        results.AddRange(await DownloadSemesterAsync("s1", downloadDir));
        results.AddRange(await DownloadSemesterAsync("s2", downloadDir));

        return results;
    }

    private async Task<List<ScheduleClass>> DownloadSemesterAsync(
        string semester,
        string outputFolder)
    {
        string url =
            $"{_url}{semester}";

        string formSelector =
            semester == "s1"
                ? "form#theForm"
                : "form[name='Seleccion']";

        int retryCount = 0;

        while (retryCount < MaxRetries)
        {
            // IMPORTANTE: se reinicia en cada intento para no arrastrar
            // chunks ya añadidos de un intento anterior fallido (evita duplicados)
            var classes = new List<ScheduleClass>();

            try
            {
                using var playwright = await Playwright.CreateAsync();

                await using var browser =
                    await playwright.Chromium.LaunchAsync(
                        new BrowserTypeLaunchOptions { Headless = true });

                var context =
                    await browser.NewContextAsync(
                        new BrowserNewContextOptions { AcceptDownloads = true });

                var page = await context.NewPageAsync();

                page.SetDefaultTimeout(60000);
                page.SetDefaultNavigationTimeout(90000);

                Console.WriteLine($"[WEB] Conectando a {semester}...");

                try
                {
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = 90000
                    });
                    await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions { Timeout = 30000 });
                    Console.WriteLine("[OK] Página cargada correctamente");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[ERROR] Timeout al cargar la página: {e.Message}");
                    throw; // fallo real de carga -> reintento completo tiene sentido
                }

                var allCheckboxes = await page.QuerySelectorAllAsync("input[type='checkbox']");
                int total = allCheckboxes.Count;
                Console.WriteLine($"Total de checkboxes encontrados: {total}");

                if (total == 0)
                    throw new Exception("No se encontraron checkboxes en la página");

                for (int i = 0; i < total; i += ChunkSize)
                {
                    int end = Math.Min(i + ChunkSize - 1, total - 1);
                    Console.WriteLine($"\n[BLOQUE] {semester}: Procesando bloque {i}-{end}");

                    // Cada bloque tiene su propio try/catch: un fallo en un bloque
                    // no debe tirar los bloques ya descargados con éxito
                    try
                    {
                        try
                        {
                            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 90000 });
                            await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions { Timeout = 30000 });
                        }
                        catch (PlaywrightException)
                        {
                            Console.WriteLine($"[ADVERTENCIA] Timeout recargando página para bloque {i}, reintentando...");
                            await Task.Delay(2000);
                            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 90000 });
                            await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions { Timeout = 30000 });
                        }

                        var checkboxes = await page.QuerySelectorAllAsync("input[type='checkbox']");

                        foreach (var checkbox in checkboxes.Skip(i).Take(ChunkSize))
                        {
                            try
                            {
                                if (!(await checkbox.IsCheckedAsync()))
                                    await checkbox.CheckAsync();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ADVERTENCIA] Error marcando checkbox: {ex.Message}");
                            }
                        }

                        try
                        {
                            await page.ClickAsync("input[value='csv']");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ADVERTENCIA] Error seleccionando formato CSV: {ex.Message}");
                            continue; // saltar este bloque, seguir con el siguiente
                        }

                        string tempFile = Path.Combine(outputFolder, $"temp_{semester}_{i}.csv");

                        try
                        {
                            var downloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 30000 });
                            await page.ClickAsync("input[type='submit'][value='Enviar']");
                            var download = await downloadTask;
                            await download.SaveAsAsync(tempFile);
                            Console.WriteLine($"[OK] Descargado como {tempFile}");
                        }
                        catch (PlaywrightException)
                        {
                            Console.WriteLine($"[ADVERTENCIA] Timeout descargando bloque {i}, continuando...");
                            continue;
                        }

                        classes.AddRange(await ParseCsvAsync(tempFile));

                        try { File.Delete(tempFile); } catch { /* no crítico */ }

                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        // Fallo puntual de un bloque: se registra y se sigue,
                        // no se aborta todo el semestre por un bloque
                        Console.WriteLine($"[ADVERTENCIA] Error en bloque {i}-{end}: {ex.Message}. Continuando...");
                        continue;
                    }
                }

                Console.WriteLine($"[OK] {semester} completado. Clases obtenidas: {classes.Count}");
                return classes; // éxito
            }
            catch (Exception ex)
            {
                retryCount++;
                Console.WriteLine($"[ERROR] Fallo fatal en {semester} (intento {retryCount}): {ex.Message}");

                if (retryCount >= MaxRetries)
                {
                    Console.WriteLine($"[ERROR] Se agotaron los reintentos para {semester}.");
                    throw;
                }

                Console.WriteLine("[ESPERA] Esperando 5 segundos antes de reintentar...");
                await Task.Delay(5000);
            }
        }

        return new List<ScheduleClass>();
    }

    private async Task<List<ScheduleClass>> ParseCsvAsync(string path)
    {
        var result = new List<ScheduleClass>();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim
        };

        // Igual que InforFormatter.py: encoding="latin1" para evitar
        // corrupción de tildes/ñ en asignaturas y aulas
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var latin1 = Encoding.GetEncoding("ISO-8859-1");

        using var reader = new StreamReader(path, latin1);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<dynamic>();

        foreach (var record in records)
        {
            try
            {
                var dict = (IDictionary<string, object>)record;

                string subject = dict.ContainsKey("Subject") ? dict["Subject"]?.ToString()?.Trim() ?? "" : "";
                string startDate = dict.ContainsKey("Start Date") ? dict["Start Date"]?.ToString()?.Trim() ?? "" : "";
                string startTime = dict.ContainsKey("Start Time") ? dict["Start Time"]?.ToString()?.Trim() ?? "" : "";
                string endTime = dict.ContainsKey("End Time") ? dict["End Time"]?.ToString()?.Trim() ?? "" : "";
                string location = dict.ContainsKey("Location") ? dict["Location"]?.ToString()?.Trim() ?? "" : "";

                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(startDate))
                    continue;

                var date = DateTime.ParseExact(
                    startDate, "dd/MM/yyyy", CultureInfo.InvariantCulture)
                    .ToString("dd/MM/yyyy");

                // Mismo comportamiento que InforFormatter.py: "Alg" -> "Algo"
                // (antes esto NO se hacía aquí y sí se hacía, por error, en Math)
                subject = System.Text.RegularExpressions.Regex.Replace(
                    subject, @"^Alg\b", "Algo");

                result.Add(new ScheduleClass
                {
                    Day = date,
                    Start = NormalizarHoraDecimal(startTime),
                    End = NormalizarHoraDecimal(endTime),
                    Subject = subject,
                    Room = location
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Fila ignorada: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Replica exactamente la lógica de InforFormatter.py:
    /// la hora viene como número decimal (ej. 10.30 = 10h y 30% de 60min = 10:18),
    /// NO como "10:30" literal. Un simple Replace(".", ":") da resultados incorrectos.
    /// </summary>
    private string NormalizarHoraDecimal(string hora)
    {
        try
        {
            double h = double.Parse(
                hora.Trim().Replace(",", "."),
                CultureInfo.InvariantCulture);

            int horaEntera = (int)h;
            int minutos = (int)Math.Round((h - horaEntera) * 60);

            return $"{horaEntera:D2}:{minutos:D2}";
        }
        catch
        {
            return "";
        }
    }
}