using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

// Settings
int productsProcessAtOnce = 10;
var inputPath = Path.Combine(Environment.CurrentDirectory, "productos.json");
var outputPath = Path.Combine(Environment.CurrentDirectory, "productos_completos.json");
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

// Program
var inputJson = await File.ReadAllTextAsync(inputPath);
var products = JsonSerializer.Deserialize<List<Producto>>(inputJson, jsonOptions)
    ?? throw new InvalidOperationException($"No se pudieron leer productos desde {inputPath}.");

if (products.Any(product => string.IsNullOrWhiteSpace(product.Url) ||
                            !Uri.TryCreate(product.Url, UriKind.Absolute, out _)))
{
    throw new InvalidOperationException("El archivo productos.json contiene una URL vacía o inválida.");
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(
    new BrowserTypeLaunchOptions { Headless = true });

var context = await browser.NewContextAsync();

await context.RouteAsync("**/*", async route =>
    {
        var resourceType = route.Request.ResourceType;
        if (resourceType is "document" or "script" or "fetch" or "xhr")
        {
            await route.ContinueAsync();
        }
        else
        {
            await route.AbortAsync();
        }
    });

await Parallel.ForEachAsync(
    products,
    new ParallelOptions
    {
        MaxDegreeOfParallelism = productsProcessAtOnce
    },
    ProcessProduct);

var outputJson = JsonSerializer.Serialize(products, new JsonSerializerOptions
{
    WriteIndented = true
});

await File.WriteAllTextAsync(outputPath, outputJson);

Console.WriteLine($"Archivo JSON guardado en: {outputPath}");

int missingSkus = products.Count(p => p.Sku is null);

if (missingSkus > 0)
{
    throw new InvalidOperationException(
        $"No se obtuvieron SKU para {missingSkus} de {products.Count} productos. " +
        $"El archivo generado contiene null en esos SKU.");
}

Console.WriteLine($"SKU obtenidos: {products.Count - missingSkus}/{products.Count}");

return;



async ValueTask ProcessProduct(Producto product, CancellationToken token)
{
    Console.WriteLine($"Iniciando producto: {product.Nombre}");

    token.ThrowIfCancellationRequested();

    var page = await context.NewPageAsync();

    var skuLocator = page.Locator(
        "span.vtex-product-identifier-0-x-product-identifier__value");

    product.Sku = null;

    var response = await page.GotoAsync(product.Url, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 40_000
    });

    System.Console.WriteLine(response?.Status);

    await skuLocator.WaitForAsync(new LocatorWaitForOptions
    {
        State = WaitForSelectorState.Attached,
        Timeout = 60_000
    });

    token.ThrowIfCancellationRequested();

    var sku = (await skuLocator.InnerTextAsync()).Trim();

    if (string.IsNullOrWhiteSpace(sku))
    {
        throw new InvalidOperationException("La página devolvió un SKU vacío.");
    }

    product.Sku = sku;

    if (product.Sku is null)
    {
        Console.Error.WriteLine($"No se pudo obtener el SKU de: {product.Url}");
    }
    else
    {
        Console.WriteLine($"Producto {product.Nombre} completado. SKU {product.Sku}");
    }

    await Task.Delay(TimeSpan.FromMilliseconds(250), token);
}

public sealed class Producto
{
    public string Nombre { get; set; } = string.Empty;
    public string Marca { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("SKU")]
    public string? Sku { get; set; }
}

