using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

// Settings
int productsProcessAtOnce = 10;
var inputPath = Path.Combine(Environment.CurrentDirectory, "productos.json");
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

// Program
var inputJson = await File.ReadAllTextAsync(inputPath);
var products = JsonSerializer.Deserialize<List<Producto>>(inputJson, jsonOptions)
    ?? throw new InvalidOperationException($"No se pudieron leer productos desde {inputPath}.");

var startIndex = 1;
var productCount = products.Count;

if (args.Length != 0)
{
    if (args.Length != 2 ||
        !int.TryParse(args[0], out startIndex) ||
        !int.TryParse(args[1], out productCount) ||
        startIndex < 1 ||
        productCount < 1)
    {
        throw new ArgumentException(
            "Se esperaban dos argumentos enteros positivos: número inicial del producto y cantidad.");
    }

    if (startIndex > products.Count)
    {
        throw new ArgumentOutOfRangeException(
            nameof(startIndex),
            $"El producto inicial ({startIndex}) supera el total de productos ({products.Count}).");
    }
}

var productsToProcess = products
    .Skip(startIndex - 1)
    .Take(productCount)
    .ToList();
var endIndex = startIndex + productsToProcess.Count - 1;
var outputPath = Path.Combine(
    Environment.CurrentDirectory,
    $"productos_completos_{startIndex}_a_{endIndex}.json");

if (productsToProcess.Any(product => string.IsNullOrWhiteSpace(product.Url) ||
                                    !Uri.TryCreate(product.Url, UriKind.Absolute, out _)))
{
    throw new InvalidOperationException(
        $"El rango seleccionado ({startIndex}-{endIndex}) " +
        "contiene una URL vacía o inválida.");
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(
    new BrowserTypeLaunchOptions { Headless = true, SlowMo = 1_000 });

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
    productsToProcess,
    new ParallelOptions
    {
        MaxDegreeOfParallelism = productsProcessAtOnce
    },
    ProcessProduct);

var outputJson = JsonSerializer.Serialize(productsToProcess, new JsonSerializerOptions
{
    WriteIndented = true
});

await File.WriteAllTextAsync(outputPath, outputJson);

Console.WriteLine($"Archivo JSON guardado en: {outputPath}");

int missingSkus = productsToProcess.Count(p => p.Sku is null);

if (missingSkus > 0)
{
    System.Console.WriteLine(
        $"No se obtuvieron SKU para {missingSkus} de {productsToProcess.Count} productos procesados. " +
        $"El archivo generado contiene null en esos SKU.");
}

Console.WriteLine(
    $"SKU obtenidos: {productsToProcess.Count - missingSkus}/{productsToProcess.Count} " +
    $"(productos {startIndex}-{endIndex})");

return;



async ValueTask ProcessProduct(Producto product, CancellationToken token)
{
    Console.WriteLine($"Iniciando producto: {product.Nombre}");

    token.ThrowIfCancellationRequested();

    var page = await context.NewPageAsync();

    var skuLocator = page.Locator(
        "span.vtex-product-identifier-0-x-product-identifier__value");

    product.Sku = null;

    try
    {
        var response = await page.GotoAsync(product.Url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30_000
        });

        if (response?.Status == 404)
        {
            Console.WriteLine($"Page not found for product {product.Nombre} con url {product.Url}.");
            return;
        }
        else if (response?.Status is < 200 or >= 300)
        {
            throw new Exception("The response from a page was other than 404 or any 2xx.");
        }
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine($"A timeout ocurred navigating to url {product.Url} para el producto {product.Nombre}.");
        return;
    }



    try
    {
        await skuLocator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 40_000
        });
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine($"A timeout ocurred waiting for selector in product {product.Nombre} con url {product.Url}.");
        return;
    }


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
