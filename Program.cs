using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

var baseUrl = "https://www.vea.com.ar/electro";
var products = new List<Producto>();

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(
    new BrowserTypeLaunchOptions { Headless = true });

var page = await browser.NewPageAsync();
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
    var pageNumbers = await page
        .Locator("button[class*='fetchMoreOptionItem']")
        .EvaluateAllAsync<int[]>("buttons => buttons.map(button => Number(button.getAttribute('value'))).filter(Number.isInteger)");

    if (pageNumbers.Length == 0)
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
Console.WriteLine(JsonSerializer.Serialize(products, new JsonSerializerOptions
{
    WriteIndented = true
}));


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

        var cards = page.Locator(productSelector);
        await cards.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 30_000
        });

        var pageProducts = await cards.EvaluateAllAsync<ProductData[]>(
            """
            cards => cards.map(card => ({
                Nombre: card.getAttribute('data-cnstrc-item-name')?.trim() ?? '',
                Marca: card.querySelector('.vtex-product-summary-2-x-productBrandName')?.textContent?.trim() ?? '',
                Url: card.querySelector('a.vtex-product-summary-2-x-clearLink')?.href ?? ''
            }))
            """);

        foreach (var product in pageProducts)
        {
            if (string.IsNullOrWhiteSpace(product.Nombre) ||
                string.IsNullOrWhiteSpace(product.Marca) ||
                string.IsNullOrWhiteSpace(product.Url))
            {
                throw new InvalidOperationException(
                    $"Faltan datos de un producto en la página {pageNumber}: {JsonSerializer.Serialize(product)}");
            }

            products.Add(new Producto(product.Nombre, product.Marca, product.Url));
        }

        Console.WriteLine($"Página {pageNumber}/{pageCount}: {pageProducts.Length} productos");
    }
}

public sealed class Producto(string nombre, string marca, string url)
{
    public string Nombre { get; } = nombre;
    public string Marca { get; } = marca;
    public string Url { get; } = url;
}

internal sealed record ProductData(string Nombre, string Marca, string Url);
