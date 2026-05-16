using System.Linq;
using CsvHelper;
using Models;
using Scrapers;
using System.Globalization;

var info = new Informatica();
var math = new MathScrapper();

var clasesInfo =
    await info.DownloadSchedulesAsync();

var clasesMath =
    await math.DownloadSchedulesAsync();

var finalClasses =
    clasesInfo
        .Concat(clasesMath)
        .ToList();

await using var writer =
    new StreamWriter("horario_final.csv");

await using var csv =
    new CsvWriter(
        writer,
        CultureInfo.InvariantCulture);

await csv.WriteRecordsAsync(finalClasses);

Console.WriteLine(
    $"Clases totales: {finalClasses.Count}");