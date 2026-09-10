using System;
using CsvHelper;
using Microsoft.Playwright;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Models;

namespace Scrapers;

public class MathScrapper
{
    private readonly string? _url;

    private const int MaxRetries = 3;

    // Solo se aceptan archivos con el formato exacto ASIGNATURA_Listado_de_clases.xls
    // (p. ej. RNEDO_Listado_de_clases.xls). El código de asignatura debe ser un único
    // bloque en mayúsculas/dígitos, sin espacios ni guiones bajos. Así se descartan
    // variantes como "Patron_Listado_de_clases.xls", "RNEDO_PL1_Listado_de_clases.xls"
    // o "RNEDO_Listado_de_clases (1).xls".
    private static readonly Regex FileNameRegex = new(
        @"^[A-ZÑ0-9]+_Listado_de_clases\.xls$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public MathScrapper()
    {
        _url = Environment.GetEnvironmentVariable("SCRAPER_MATEMATICAS_URL");
    }


    public async Task<List<ScheduleClass>> DownloadSchedulesAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var downloadDir = Path.GetFullPath("dataM");
        Directory.CreateDirectory(downloadDir);

        int retryCount = 0;

        while (retryCount < MaxRetries)
        {
            var downloadedFiles = new HashSet<string>();
            var fileNamesToDownload = new List<string>();

            try
            {
                Console.WriteLine($"[REINTENTO] MathScrapper - Intento {retryCount + 1} de {MaxRetries}...");

                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true });
                var context = await browser.NewContextAsync(
                    new BrowserNewContextOptions { AcceptDownloads = true });
                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(60000);

                Console.WriteLine("[WEB] Abriendo SharePoint...");

                try
                {
                    await page.GotoAsync(_url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = 120000
                    });
                    await page.WaitForSelectorAsync("[role='row']", new PageWaitForSelectorOptions { Timeout = 60000 });
                    Console.WriteLine("[OK] SharePoint cargado correctamente");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[ERROR] Timeout al cargar SharePoint: {e.Message}");
                    throw new Exception("No se pudo conectar a SharePoint");
                }

                await CollectAllFileNamesAsync(page, fileNamesToDownload);
                Console.WriteLine($"[INFO] Archivos detectados: {fileNamesToDownload.Count}");

                if (fileNamesToDownload.Count == 0)
                    throw new Exception("No se detectó ningún archivo 'ASIGNATURA_Listado_de_clases.xls'");

                foreach (var fileName in fileNamesToDownload)
                {
                    var filePath = Path.GetFullPath(Path.Combine(downloadDir, fileName.Replace(" ", "_")));
                    if (!downloadedFiles.Add(filePath)) continue;

                    await DownloadFileByNameAsync(page, fileName, filePath);
                }

                await browser.CloseAsync();

                Console.WriteLine($"[OK] MathScrapper completado. Descargados: {downloadedFiles.Count} archivos");
                return ProcessFiles(downloadedFiles.ToList());
            }
            catch (Exception ex)
            {
                retryCount++;
                Console.WriteLine($"[ERROR] Error fatal en MathScrapper (intento {retryCount}): {ex.Message}");

                if (retryCount >= MaxRetries)
                {
                    Console.WriteLine("[ERROR] Se agotaron los reintentos. MathScrapper falló.");
                    throw;
                }

                Console.WriteLine("[ESPERA] Esperando 10 segundos antes de reintentar...");
                await Task.Delay(10000);
            }
        }

        return new List<ScheduleClass>();
    }

    private async Task CollectAllFileNamesAsync(IPage page, List<string> result)
    {
        var scrollContainer =
            await page.QuerySelectorAsync("div[class^='list_']")
            ?? await page.QuerySelectorAsync("body");

        int noChangeStreak = 0;
        int lastFileCount = 0;
        const int maxScrollAttempts = 200;

        for (int i = 0; i < maxScrollAttempts; i++)
        {
            var rows = await page.QuerySelectorAllAsync("[role='row']");
            foreach (var row in rows)
            {
                try
                {
                    var text = await row.InnerTextAsync();
                    var rawName = text.Split('\n')[0].Trim();

                    if (FileNameRegex.IsMatch(rawName) && !result.Contains(rawName))
                    {
                        result.Add(rawName);
                    }
                }
                catch { /* fila puntual ilegible, seguir */ }
            }

            Console.WriteLine($"[SCROLL {i}] Filas: {rows.Count} | Archivos: {result.Count}");

            try
            {
                await scrollContainer!.EvaluateAsync("el => el.scrollBy(0, 100)");
                await page.WaitForTimeoutAsync(300);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ADVERTENCIA] Error en scroll: {e.Message}");
                break;
            }

            // Se usa el número de ARCHIVOS detectados (no de filas) para decidir
            // si parar, ya que en listas virtualizadas rows.Count puede
            // mantenerse constante aunque sigan apareciendo archivos nuevos.
            if (result.Count == lastFileCount)
            {
                noChangeStreak++;
                if (noChangeStreak >= 20) break;
            }
            else
            {
                noChangeStreak = 0;
                lastFileCount = result.Count;
            }
        }
    }

    private async Task DownloadFileByNameAsync(IPage page, string fileName, string filePath)
    {
        Console.WriteLine($"[DESCARGA] {fileName}");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var rows = await page.QuerySelectorAllAsync("[role='row']");
                IElementHandle? targetRow = null;

                foreach (var row in rows)
                {
                    var text = await row.InnerTextAsync();
                    if (text.Split('\n')[0].Trim() == fileName)
                    {
                        targetRow = row;
                        break;
                    }
                }

                targetRow ??= await ScrollUntilRowVisibleAsync(page, fileName);

                if (targetRow == null)
                {
                    Console.WriteLine($"[ERROR] No encontrada: {fileName}");
                    return;
                }

                await targetRow.ScrollIntoViewIfNeededAsync();
                await page.WaitForTimeoutAsync(500);

                await targetRow.ClickAsync(new ElementHandleClickOptions { Button = MouseButton.Right });
                await page.WaitForTimeoutAsync(400);

                var downloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 30000 });
                await page.GetByRole(AriaRole.Menuitem, new() { Name = "Descargar", Exact = true }).First.ClickAsync();

                var download = await downloadTask;
                await download.SaveAsAsync(filePath);

                await page.ClickAsync("body");
                await page.WaitForTimeoutAsync(400);
                return; // éxito
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[REINTENTO {attempt + 1}] {fileName}: {ex.Message.Split('\n')[0]}");
                try { await page.ClickAsync("body"); } catch { }
                await page.WaitForTimeoutAsync(600);
            }
        }
    }

    private async Task<IElementHandle?> ScrollUntilRowVisibleAsync(IPage page, string fileName)
    {
        var scrollContainer =
            await page.QuerySelectorAsync("div[class^='list_']")
            ?? await page.QuerySelectorAsync("body");

        await scrollContainer!.EvaluateAsync("el => el.scrollTo(0, 0)");
        await page.WaitForTimeoutAsync(500);

        for (int i = 0; i < 300; i++)
        {
            var rows = await page.QuerySelectorAllAsync("[role='row']");
            foreach (var row in rows)
            {
                try
                {
                    var text = await row.InnerTextAsync();
                    if (text.Split('\n')[0].Trim() == fileName)
                        return row;
                }
                catch { }
            }

            await scrollContainer.EvaluateAsync("el => el.scrollBy(0, 200)");
            await page.WaitForTimeoutAsync(300);
        }

        return null;
    }

    private List<ScheduleClass> ProcessFiles(List<string> files)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var result = new List<ScheduleClass>();

        foreach (var file in files)
        {
            try
            {
                var fullPath = Path.GetFullPath(file);
                Console.WriteLine($"[PROCESANDO] {fullPath}");

                if (!File.Exists(fullPath))
                {
                    Console.WriteLine($"[ADVERTENCIA] NO EXISTE: {fullPath}");
                    continue;
                }

                using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read);
                using var reader = ExcelReaderFactory.CreateReader(stream);

                var dataset = reader.AsDataSet();
                var table = dataset.Tables[0];

                Console.WriteLine($"Sheets: {dataset.Tables.Count} | Rows: {table.Rows.Count}");

                string code = Path.GetFileNameWithoutExtension(file)
                    .Split('_')[0]
                    .Trim()
                    .ToUpperInvariant();

                for (int i = 4; i < table.Rows.Count; i++)
                {
                    try
                    {
                        var row = table.Rows[i];

                        string grupo = row[1]?.ToString()?.Trim() ?? "";
                        string fecha = row[5]?.ToString()?.Trim() ?? "";
                        string hora = row[6]?.ToString()?.Trim() ?? "";
                        string aula = row[7]?.ToString()?.Trim() ?? "";

                        if (string.IsNullOrWhiteSpace(fecha) || string.IsNullOrWhiteSpace(hora))
                            continue;

                        hora = hora
                            .Replace("h", ":")
                            .Replace("\u2019", "")
                            .Replace("'", "")
                            .Trim();

                        if (!hora.Contains("-"))
                            continue;

                        var parts = hora.Split('-');

                        var date = ParseFecha(row[5]);
                        if (date == null)
                        {
                            Console.WriteLine($"[FILA {i} ERROR] Fecha no reconocida: '{fecha}'");
                            continue;
                        }

                        result.Add(new ScheduleClass
                        {
                            Day = date,
                            Start = parts[0].Trim(),
                            End = parts[1].Trim(),

                            Subject = $"{code}.{grupo}",
                            Room = aula.Replace("Aula", "").Trim()
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FILA {i} ERROR] {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR FILE] {ex.Message}");
            }
            finally
            {
                try { File.Delete(Path.GetFullPath(file)); }
                catch (Exception fe) { Console.WriteLine($"[WARN] No se pudo borrar {file}: {fe.Message}"); }
            }
        }

        Console.WriteLine($"TOTAL PARSED: {result.Count}");
        return result;
    }

    private static string? ParseFecha(object? cell)
    {
        switch (cell)
        {
            case DateTime dt:
                return dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

            case double serial:
                return DateTime.FromOADate(serial).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

            case string s when DateTime.TryParseExact(
                s.Trim().Split(' ')[0],
                new[] { "dd/MM/yyyy", "d/M/yyyy" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed):
                return parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

            default:
                return null;
        }
    }

    public async Task ExportCsvAsync(List<ScheduleClass> classes, string output)
    {
        await using var writer = new StreamWriter(output);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(classes);
    }
}