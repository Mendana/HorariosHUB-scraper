using Microsoft.Playwright;
using Models;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace Scrapers;

public class Informatica
{
    private const int MaxRetries = 3;
    private const int ChunkSize = 100;

    public async Task<List<ScheduleClass>> DownloadSchedulesAsync(
        string outputFolder = "dataI")
    {
        var downloadDir = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(downloadDir);

        Console.WriteLine($"[DIR] Working dir: {Directory.GetCurrentDirectory()}");
        Console.WriteLine($"[DIR] dataI path: {downloadDir}");

        var results = new List<ScheduleClass>();

        results.AddRange(
            await DownloadSemesterAsync("s1", downloadDir));

        results.AddRange(
            await DownloadSemesterAsync("s2", downloadDir));

        return results;
    }

    private async Task<List<ScheduleClass>> DownloadSemesterAsync(
        string semester,
        string outputFolder)
    {
        var classes = new List<ScheduleClass>();

        string url =
            $"https://gobierno.ingenieriainformatica.uniovi.es/grado/plan/?y=25-26&t={semester}";

        string formSelector =
            semester == "s1"
                ? "form#theForm"
                : "form[name='Seleccion']";

        int retryCount = 0;

        while (retryCount < MaxRetries)
        {
            try
            {
                using var playwright =
                    await Playwright.CreateAsync();

                await using var browser =
                    await playwright.Chromium.LaunchAsync(
                        new BrowserTypeLaunchOptions
                        {
                            Headless = true
                        });

                var context =
                    await browser.NewContextAsync(
                        new BrowserNewContextOptions
                        {
                            AcceptDownloads = true
                        });

                var page = await context.NewPageAsync();

                page.SetDefaultTimeout(60000);
                page.SetDefaultNavigationTimeout(90000);

                Console.WriteLine($"[WEB] {semester}");

                await page.GotoAsync(
                    url,
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = 90000
                    });

                await page.WaitForSelectorAsync(formSelector);

                var allCheckboxes =
                    await page.QuerySelectorAllAsync(
                        "input[type='checkbox']");

                int total = allCheckboxes.Count;

                for (int i = 0; i < total; i += ChunkSize)
                {
                    int end = Math.Min(i + ChunkSize - 1, total - 1);

                    Console.WriteLine($"[BLOQUE] {semester}: {i}-{end}");

                    await page.GotoAsync(
                        url,
                        new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.Load
                        });

                    await page.WaitForSelectorAsync(formSelector);

                    var checkboxes =
                        await page.QuerySelectorAllAsync(
                            "input[type='checkbox']");

                    foreach (var checkbox in
                             checkboxes.Skip(i).Take(ChunkSize))
                    {
                        try
                        {
                            if (!(await checkbox.IsCheckedAsync()))
                                await checkbox.CheckAsync();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[WARN] Checkbox skip: {ex.Message}");
                        }
                    }

                    await page.ClickAsync("input[value='csv']");

                    var downloadTask = page.WaitForDownloadAsync();

                    await page.ClickAsync(
                        "input[type='submit'][value='Enviar']");

                    var download = await downloadTask;

                    string tempFile = Path.Combine(
                        outputFolder,
                        $"temp_{semester}_{i}.csv");

                    await download.SaveAsAsync(tempFile);

                    Console.WriteLine($"[GUARDADO] {tempFile}");

                    classes.AddRange(await ParseCsvAsync(tempFile));

                    File.Delete(tempFile);

                    await Task.Delay(1000);
                }

                return classes;
            }
            catch (Exception ex)
            {
                retryCount++;

                Console.WriteLine($"[ERROR] {semester}: {ex.Message}");

                if (retryCount >= MaxRetries)
                    throw;

                await Task.Delay(5000);
            }
        }

        return classes;
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

        using var reader = new StreamReader(path);
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

                if (string.IsNullOrWhiteSpace(subject) ||
                    string.IsNullOrWhiteSpace(startDate))
                    continue;

                // Fecha: "09/09/2025" ? "09/09/2025"
                var date = DateTime.ParseExact(
                    startDate, "dd/MM/yyyy", CultureInfo.InvariantCulture)
                    .ToString("dd/MM/yyyy");

                // Hora: "11.00" ? "11:00"
                string start = startTime.Replace(".", ":");
                string end = endTime.Replace(".", ":");

                result.Add(new ScheduleClass
                {
                    Day = date,
                    Start = start,
                    End = end,
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
}