using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Models;
using Scrapers;
using dotenv;
using dotenv.net;

var envPath = Path.Combine(
    Directory.GetCurrentDirectory(),
    "..", "..", ".env"
);

if (!File.Exists(envPath))
{
    envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    Console.WriteLine($"[DEBUG] Reintentando en: {Path.GetFullPath(envPath)}");
}

if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
        var parts = line.Split('=', 2);
        if (parts.Length == 2)
        {
            Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
        }
    }
}
else
{
    Console.WriteLine($"[ERROR] ❌ .env NO ENCONTRADO");
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string ToCsv(IEnumerable<ScheduleClass> classes)
{
    static string Escape(string f) =>
        f.Contains(',') || f.Contains('"') || f.Contains('\n')
            ? $"\"{f.Replace("\"", "\"\"")}\""
            : f;

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Day,Start,End,Subject,Room");
    foreach (var c in classes)
        sb.AppendLine($"{Escape(c.Day)},{Escape(c.Start)},{Escape(c.End)},{Escape(c.Subject)},{Escape(c.Room)}");
    return sb.ToString();
}

app.MapPost("/scrape", async () =>
{

    var info = new Informatica();
    var math = new MathScrapper();

    var clasesInfo = await info.DownloadSchedulesAsync();
    var clasesMath = await math.DownloadSchedulesAsync();

    var finalClasses = clasesInfo
        .Concat(clasesMath)
        .ToList();

    var csv = ToCsv(finalClasses);
    return Results.Text(csv, "text/csv");

});

app.MapPost("/groups", async (HttpRequest request) =>
{
    try
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        // Aceptar UO en el body como texto plano o como query param
        var uo = body.Trim();
        if (string.IsNullOrEmpty(uo))
            uo = request.Query["uo"].ToString().Trim();

        if (string.IsNullOrEmpty(uo))
            return Results.BadRequest("Se requiere el UO");

        var uoFormatted = uo.ToLower().StartsWith("uo")
            ? "Uo" + uo[2..]
            : "Uo" + uo;

        var scraper = new GroupScraper();
        var result = await scraper.GetGroupsAsync(uoFormatted);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Subject,Group");

        foreach (var cls in result.Classes)
        {
            string subject, group;

            if (cls.Contains('-'))
            {
                var parts = cls.Split('-', 2);
                subject = parts[0];
                group = parts[1];
            }
            else
            {
                var dotIdx = cls.IndexOf('.');
                subject = cls[..dotIdx];
                group = cls[(dotIdx + 1)..];
            }

            sb.AppendLine($"{subject},{group}");
        }

        return Results.Text(sb.ToString(), "text/csv");
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run("http://0.0.0.0:3003");