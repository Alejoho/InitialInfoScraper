using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

var baseUrl = "https://www.vea.com.ar/electro";
var products = new List<Producto>();

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(
    new BrowserTypeLaunchOptions { Headless = true });

var page = await browser.NewPageAsync();

// await page.RouteAsync("**/*", async route =>
//     {
//         var resourceType = route.Request.ResourceType;
//         if (resourceType is "document" or "script" or "fetch" or "xhr")
//         {
//             await route.ContinueAsync();
//         }
//         else
//         {
//             await route.AbortAsync();
//         }
//     });

await page.GotoAsync($"{baseUrl}?page=1", new PageGotoOptions
{
    WaitUntil = WaitUntilState.DOMContentLoaded
});

var productSelector = "#gallery-layout-container [data-af-element='search-result']";
await page.Locator(productSelector).First.WaitForAsync(new LocatorWaitForOptions
{
    State = WaitForSelectorState.Attached,
    Timeout = 30_000
});

var paginationText = await page
    .Locator("button[class*='fetchMoreOpButton']")
    .InnerTextAsync();
var pageCountMatch = Regex.Match(paginationText, @"de\s+(\d+)", RegexOptions.IgnoreCase);
int pageCount;

if (!pageCountMatch.Success)
{
    var pageButtons = await page
        .Locator("button[class*='fetchMoreOptionItem']")
        .AllAsync();
    var pageNumbers = new List<int>();

    foreach (var button in pageButtons)
    {
        var value = await button.GetAttributeAsync("value");
        if (int.TryParse(value, out var number))
        {
            pageNumbers.Add(number);
        }
    }

    if (pageNumbers.Count == 0)
    {
        throw new InvalidOperationException("No se pudo determinar el total de páginas desde la navegación.");
    }

    pageCount = pageNumbers.Max();
}
else
{
    pageCount = int.Parse(pageCountMatch.Groups[1].Value);
}

await ScrapePagesAsync(page, pageCount, baseUrl, productSelector, products);

Console.WriteLine($"Páginas procesadas: {pageCount}");
Console.WriteLine($"Productos obtenidos: {products.Count}");
var json = JsonSerializer.Serialize(products, new JsonSerializerOptions
{
    WriteIndented = true
});
var outputPath = Path.Combine(Environment.CurrentDirectory, "productos.json");
await File.WriteAllTextAsync(outputPath, json);

Console.WriteLine($"Archivo JSON guardado en: {outputPath}");

return;



static async Task ScrapePagesAsync(
    IPage page,
    int pageCount,
    string baseUrl,
    string productSelector,
    List<Producto> products)
{
    for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
    {
        await page.GotoAsync($"{baseUrl}?page={pageNumber}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });

        await page.EvaluateAsync(
            "window.scrollTo(0, 1000)");

        var paginationLocator = page.Locator("button[class*='fetchMoreOpButton']");
        await paginationLocator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });

        await paginationLocator.ScrollIntoViewIfNeededAsync();

        var cardsLocator = page.Locator(productSelector);

        var cardLocators = await cardsLocator.AllAsync();

        System.Console.WriteLine(cardLocators.Count);

        foreach (var cardLocator in cardLocators)
        {
            var name = await cardLocator.GetAttributeAsync("data-cnstrc-item-name");
            if (string.IsNullOrWhiteSpace(name))
            {
                var productName = cardLocator.Locator(".vtex-product-summary-2-x-productBrand");

                if (await productName.CountAsync() > 0)
                {
                    name = await productName.InnerTextAsync();
                }
            }

            var brandLocator = cardLocator.Locator(".vtex-product-summary-2-x-productBrandName");
            await brandLocator.WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached
                });
            var brand = await brandLocator.CountAsync() > 0
                ? await brandLocator.InnerTextAsync()
                : string.Empty;

            var href = await cardLocator
                .Locator("a.vtex-product-summary-2-x-clearLink")
                .GetAttributeAsync("href");
            var url = string.IsNullOrWhiteSpace(href)
                ? string.Empty
                : new Uri(new Uri(page.Url), href).AbsoluteUri;

            name = name?.Trim();
            brand = brand.Trim();

            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(brand) ||
                string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException(
                    $"Faltan datos de un producto en la página {pageNumber}: " +
                    JsonSerializer.Serialize(new { Nombre = name, Marca = brand, Url = url }));
            }

            products.Add(new Producto
            {
                Nombre = name,
                Marca = brand,
                Url = url
            });
        }

        Console.WriteLine($"Página {pageNumber}/{pageCount}: {cardLocators.Count} productos");
    }
}


public sealed class Producto
{
    public string Nombre { get; set; } = string.Empty;
    public string Marca { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
