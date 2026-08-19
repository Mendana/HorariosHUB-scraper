// Scrapers/GroupScraper.cs
using Microsoft.Playwright;
using System.Text.RegularExpressions;
using ExcelDataReader;
using System.Text;

namespace Scrapers;

public class GroupScraper
{
    private static readonly string[] InfoSiteUrls =
    (Environment.GetEnvironmentVariable("INFO_SITE_URL")
     ?? "https://gobierno.ingenieriainformatica.uniovi.es/grado/gd/?y=25-26&t=s1,https://gobierno.ingenieriainformatica.uniovi.es/grado/gd/?y=25-26&t=s2")
    .Split(',', StringSplitOptions.TrimEntries);

    private static readonly string MathSiteUrl =
        Environment.GetEnvironmentVariable("MATH_SITE_URL")
        ?? "https://unioviedo-my.sharepoint.com/:f:/g/personal/perezfernandez_uniovi_es/Eu9qlYNQEYhMi2gDAxmrmvABPSoVDkYi4cCTXf_ZVyql9w?e=a3ZGBs";

    private const int MaxRetries = 3;

    public async Task<GroupResult> GetGroupsAsync(string uo)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true });

        var infoClasses = await ScrapeInfoAsync(context, uo);
        var mathClasses = await ScrapeMathAsync(context, uo);

        await context.CloseAsync();

        var allClasses = infoClasses.Concat(mathClasses)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        return new GroupResult
        {
            Success = allClasses.Count > 0,
            Uo = uo,
            Classes = allClasses,
            Sources = new GroupSources
            {
                Gobierno = infoClasses.Count > 0,
                Sharepoint = mathClasses.Count > 0
            }
        };
    }

    // ?? INFO SERVICE ?????????????????????????????????????????????????????????

    private async Task<List<string>> ScrapeInfoAsync(IBrowserContext context, string uo)
    {
        var allClasses = new List<string>();

        foreach (var url in InfoSiteUrls)
        {
            var classes = await ScrapeInfoUrlAsync(context, uo, url);
            allClasses.AddRange(classes);
        }

        return allClasses.Distinct().ToList();
    }

    private async Task<List<string>> ScrapeInfoUrlAsync(IBrowserContext context, string uo, string url)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(60000);
            page.SetDefaultNavigationTimeout(120000);

            try
            {
                Console.WriteLine($"[INFO] Intento {attempt}/{MaxRetries} - {url}");
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                // Buscar <a> cuyo texto sea el UO y navegar a su href
                var link = page.Locator($"a:has-text(\"{uo}\")").First;
                var href = await link.GetAttributeAsync("href");

                if (string.IsNullOrEmpty(href))
                {
                    Console.WriteLine($"[INFO] No se encontró href para {uo}");
                    await page.CloseAsync();
                    continue;
                }

                Console.WriteLine($"[INFO] Navegando a {href}");
                await page.GotoAsync(href, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                // Extraer texto de h1 + p ? "Grupos: Alg.T.2; SO.L.1; ..."
                var paragraph = page.Locator("h1 + p").First;
                var text = await paragraph.TextContentAsync();

                if (string.IsNullOrWhiteSpace(text) || !text.Contains(":"))
                {
                    await page.CloseAsync();
                    return [];
                }

                var classes = text.Split(':', 2)[1]
                    .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

                Console.WriteLine($"[INFO] {classes.Count} grupos encontrados en {url}");
                await page.CloseAsync();
                return classes;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INFO ERROR] Intento {attempt}: {ex.Message.Split('\n')[0]}");
                await page.CloseAsync();

                if (attempt < MaxRetries)
                    await Task.Delay(attempt * 1000);
            }
        }

        return [];
    }

    // ?? MATH SERVICE ?????????????????????????????????????????????????????????

    private async Task<List<string>> ScrapeMathAsync(IBrowserContext context, string uo)
    {
        var targetFilename = $"Lista_clases_{uo.ToUpperInvariant()}@uniovi.es.xls";
        Console.WriteLine($"[MATH] Buscando archivo: {targetFilename}");

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(60000);
            page.SetDefaultNavigationTimeout(120000);

            try
            {
                Console.WriteLine($"[MATH] Intento {attempt}/{MaxRetries}");
                await page.GotoAsync(MathSiteUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 120000 });
                await page.WaitForSelectorAsync("[role='row']", new PageWaitForSelectorOptions { Timeout = 60000 });

                var scrollContainer =
                    await page.QuerySelectorAsync("div[class^='list_']")
                    ?? await page.QuerySelectorAsync("body");

                var filePath = await ScrollAndDownloadAsync(page, scrollContainer!, targetFilename);

                await page.CloseAsync();

                if (filePath != null)
                {
                    var classes = ProcessMathFile(filePath);
                    try { File.Delete(filePath); } catch { }
                    return classes;
                }

                Console.WriteLine($"[MATH] Archivo no encontrado para {uo}");
                return [];
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MATH ERROR] Intento {attempt}: {ex.Message.Split('\n')[0]}");
                await page.CloseAsync();

                if (attempt < MaxRetries)
                    await Task.Delay(attempt * 5000);
            }
        }

        return [];
    }

    private async Task<string?> ScrollAndDownloadAsync(IPage page, IElementHandle scrollContainer, string targetFilename)
    {
        int scrollAttempts = 0;

        while (scrollAttempts < 500)
        {
            var rows = await page.QuerySelectorAllAsync("[role='row']");

            foreach (var row in rows)
            {
                try
                {
                    var text = await row.InnerTextAsync();
                    var firstLine = text.Split('\n')[0].Trim();

                    if (!firstLine.StartsWith("Lista_clases_")) continue;

                    // Truncar al .xls
                    var xlsIdx = firstLine.IndexOf(".xls");
                    if (xlsIdx >= 0) firstLine = firstLine[..(xlsIdx + 4)];

                    if (firstLine != targetFilename) continue;

                    Console.WriteLine($"[MATH] Archivo encontrado: {firstLine}");
                    return await DownloadRowFileAsync(page, row, firstLine);
                }
                catch { }
            }

            await scrollContainer.EvaluateAsync("el => el.scrollBy(0, 150)");
            await page.WaitForTimeoutAsync(300);
            scrollAttempts++;
        }

        return null;
    }

    private async Task<string?> DownloadRowFileAsync(IPage page, IElementHandle row, string filename)
    {
        try
        {
            await row.ClickAsync(new ElementHandleClickOptions { Button = MouseButton.Right });
            await page.WaitForTimeoutAsync(500);

            var downloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 60000 });
            await page.ClickAsync("text=Descargar", new PageClickOptions { Timeout = 10000 });
            var download = await downloadTask;

            var tempPath = Path.Combine(Path.GetTempPath(), filename.Replace(" ", "_").Replace("/", "_"));
            await download.SaveAsAsync(tempPath);

            Console.WriteLine($"[MATH] Descargado: {tempPath}");
            return tempPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MATH ERROR] Descarga fallida: {ex.Message.Split('\n')[0]}");
            return null;
        }
    }

    private static readonly Regex ClassPattern = new(@"^[A-Za-z]+[A-Za-z0-9]*-[A-Z]{2}\d+$", RegexOptions.Compiled);

    private List<string> ProcessMathFile(string filePath)
    {
        var classes = new HashSet<string>();

        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read);
            using var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(stream);
            var dataset = reader.AsDataSet();

            foreach (System.Data.DataTable table in dataset.Tables)
            {
                foreach (System.Data.DataRow row in table.Rows)
                {
                    foreach (var cell in row.ItemArray)
                    {
                        var value = cell?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(value)) continue;
                        if (ClassPattern.IsMatch(value) && value.Length > 3)
                            classes.Add(value);
                    }
                }
            }

            Console.WriteLine($"[MATH] {classes.Count} grupos extraídos del archivo");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MATH ERROR] Procesando archivo: {ex.Message}");
        }

        return classes.OrderBy(c => c).ToList();
    }
}

public record GroupResult
{
    public bool Success { get; init; }
    public string Uo { get; init; } = "";
    public List<string> Classes { get; init; } = [];
    public GroupSources Sources { get; init; } = new();
}

public record GroupSources
{
    public bool Gobierno { get; init; }
    public bool Sharepoint { get; init; }
}