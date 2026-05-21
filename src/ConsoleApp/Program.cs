using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Models;
using Scrapers;

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

app.Run("http://0.0.0.0:3003");