using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

const int maxAttempts = 3;
// var productDirectory = FindProductDirectory();
var inputPath = Path.Combine(Environment.CurrentDirectory, "productos.json");
var outputPath = Path.Combine(Environment.CurrentDirectory, "productos_completos.json");
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

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

var page = await browser.NewPageAsync();
await page.RouteAsync("**/*", async route =>
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

var skuLocator = page.Locator(
    "span.vtex-product-identifier-0-x-product-identifier__value");

var missingSkus = new List<Producto>();

for (var index = 0; index < products.Count; index++)
{
    var product = products[index];
    product.Sku = null;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await page.GotoAsync(product.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });

            await skuLocator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 30_000
            });

            var sku = (await skuLocator.InnerTextAsync()).Trim();

            if (string.IsNullOrWhiteSpace(sku))
            {
                throw new InvalidOperationException("La página devolvió un SKU vacío.");
            }

            product.Sku = sku;
            break;
        }
        catch (Exception exception) when (
            exception is PlaywrightException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine(
                $"Intento {attempt}/{maxAttempts} fallido para {product.Url}: {exception.Message}");

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    if (product.Sku is null)
    {
        missingSkus.Add(product);
        Console.Error.WriteLine($"No se pudo obtener el SKU de: {product.Url}");
    }
    else
    {
        Console.WriteLine($"[{index + 1}/{products.Count}] SKU {product.Sku}: {product.Nombre}");
    }

    await Task.Delay(TimeSpan.FromMilliseconds(250));
}

var outputJson = JsonSerializer.Serialize(products, new JsonSerializerOptions
{
    WriteIndented = true
});
await File.WriteAllTextAsync(outputPath, outputJson);
Console.WriteLine($"Archivo JSON guardado en: {outputPath}");

if (missingSkus.Count > 0)
{
    throw new InvalidOperationException(
        $"No se obtuvieron SKU para {missingSkus.Count} de {products.Count} productos. " +
        $"El archivo generado contiene null en esos SKU.");
}

Console.WriteLine($"SKU obtenidos: {products.Count}/{products.Count}");

static string FindProductDirectory()
{
    var searchDirectories = new[]
    {
        new DirectoryInfo(Environment.CurrentDirectory),
        new DirectoryInfo(AppContext.BaseDirectory)
    };

    foreach (var startDirectory in searchDirectories)
    {
        for (var directory = startDirectory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "productos.json")))
            {
                return directory.FullName;
            }
        }
    }

    throw new FileNotFoundException(
        "No se encontró productos.json en el directorio actual ni en los directorios del programa.");
}

public sealed class Producto
{
    public string Nombre { get; set; } = string.Empty;
    public string Marca { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("SKU")]
    public string? Sku { get; set; }
}
