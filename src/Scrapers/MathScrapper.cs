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
    private const string Url =
        "https://unioviedo-my.sharepoint.com/:f:/g/personal/perezfernandez_uniovi_es/EnRId5nKPg5DncyuhN5-xA4BWcHY0SXA6Y-AjHbFwfyLFQ?e=qwlVj6";

    public async Task<List<ScheduleClass>> DownloadSchedulesAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var downloadDir = Path.GetFullPath("dataM");
        Directory.CreateDirectory(downloadDir);

        var downloadedFiles = new HashSet<string>();

        using var playwright = await Playwright.CreateAsync();

        await using var browser =
            await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            AcceptDownloads = true
        });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(60000);

        Console.WriteLine("[WEB] Abriendo SharePoint...");

        await page.GotoAsync(Url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 120000
        });

        await page.WaitForSelectorAsync("[role='row']");

        var scrollContainer =
            await page.QuerySelectorAsync("div[class^='list_']")
            ?? await page.QuerySelectorAsync("body");

        int scrollAttempts = 0;

        while (scrollAttempts < 200)
        {
            var rows = await page.QuerySelectorAllAsync("[role='row']");

            foreach (var row in rows)
            {
                try
                {
                    var text = await row.InnerTextAsync();
                    var rawName = text.Split('\n')[0].Trim();

                    Console.WriteLine($"[INFO] Detectado: >>{rawName}<<");

                    var xlsMatch = Regex.Match(rawName, @"^(\S+_Listado_de_clases\.xls)");
                    if (!xlsMatch.Success) continue;

                    var fileName = xlsMatch.Groups[1].Value;

                    var filePath = Path.GetFullPath(
                        Path.Combine(downloadDir, fileName.Replace(" ", "_")));

                    if (!downloadedFiles.Add(filePath)) continue;

                    Console.WriteLine($"[DESCARGA] {fileName}");

                    await row.ScrollIntoViewIfNeededAsync();
                    await page.WaitForTimeoutAsync(300);

                    await row.ClickAsync(new ElementHandleClickOptions
                    {
                        Button = MouseButton.Right
                    });
                    await page.WaitForTimeoutAsync(400);

                    var downloadTask = page.WaitForDownloadAsync();

                    await page.GetByRole(AriaRole.Menuitem, new() { Name = "Descargar", Exact = true })
                              .First.ClickAsync();

                    var download = await downloadTask;
                    await download.SaveAsAsync(filePath);

                    await page.ClickAsync("body");
                    await page.WaitForTimeoutAsync(300);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR DESCARGA] {ex.Message.Split('\n')[0]}");
                    try { await page.ClickAsync("body"); } catch (Exception ce) { Console.WriteLine($"[WARN] Click body fallido: {ce.Message}"); }
                    await page.WaitForTimeoutAsync(300);
                }
            }

            await scrollContainer!.EvaluateAsync("el => el.scrollBy(0, 100)");
            await page.WaitForTimeoutAsync(300);

            scrollAttempts++;
        }

        Console.WriteLine($"[OK] Descargados: {downloadedFiles.Count} archivos");

        return ProcessFiles(downloadedFiles.ToList());
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
                    Console.WriteLine($"? NO EXISTE: {fullPath}");
                    continue;
                }

                using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read);
                using var reader = ExcelReaderFactory.CreateReader(stream);

                var dataset = reader.AsDataSet();

                Console.WriteLine($"Sheets: {dataset.Tables.Count}");

                var table = dataset.Tables[0];

                Console.WriteLine($"Rows: {table.Rows.Count}");

                for (int dbg = 0; dbg < Math.Min(10, table.Rows.Count); dbg++)
                {
                    var r = table.Rows[dbg];
                    var cols = string.Join(" | ", Enumerable.Range(0, table.Columns.Count)
                        .Select(c => $"[{c}]='{r[c]}'"));
                    Console.WriteLine($"  Fila {dbg}: {cols}");
                }

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

                        var date = DateTime.ParseExact(
                            fecha.Split(' ')[0],
                            "dd/MM/yyyy",
                            CultureInfo.InvariantCulture
                        ).ToString("dd/MM/yyyy");

                        result.Add(new ScheduleClass
                        {
                            Day = date,
                            Start = parts[0].Trim(),
                            End = parts[1].Trim(),
                            Subject = $"{NormalizeSubject(code)}.{grupo}",
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
                try { File.Delete(Path.GetFullPath(file)); } catch (Exception fe) { Console.WriteLine($"[WARN] No se pudo borrar {file}: {fe.Message}"); }
            }
        }

        Console.WriteLine($"TOTAL PARSED: {result.Count}");

        return result;
    }

    private string NormalizeSubject(string code)
    {
        code = code.Trim().ToUpperInvariant();

        return code switch
        {
            "ALG" => "Alge",
            _ => code
        };
    }

    public async Task ExportCsvAsync(List<ScheduleClass> classes, string output)
    {
        await using var writer = new StreamWriter(output);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        await csv.WriteRecordsAsync(classes);
    }
}