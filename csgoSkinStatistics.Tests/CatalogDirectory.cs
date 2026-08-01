using System.Text.Json;
using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;

namespace csgoSkinStatistics.Tests;

// A throwaway directory holding the catalog files ConstDataService loads.
//
// Tests used to write const.json/fade.json straight into the shared test working directory and
// delete them at the end of the test body. That raced every other test that builds a service (xunit
// runs test classes in parallel), and any assertion failure skipped the cleanup and left a stub
// catalog behind for whatever ran next. Each test now gets its own directory instead.
internal sealed class CatalogDirectory : IDisposable
{
    private readonly string _directory;

    private CatalogDirectory(string directory) => _directory = directory;

    public static CatalogDirectory Create(ConstData? constData = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csgoskin-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var catalogs = new CatalogDirectory(directory);
        catalogs.Write("const.json", constData ?? new ConstData());
        // ConstDataService reads these two unconditionally, so they always have to exist; empty
        // stubs are enough unless a test asks for the shipped copy.
        catalogs.Write("stickers.json", new StickerCatalog());
        catalogs.Write("skin-images.json", new Dictionary<string, string>());
        return catalogs;
    }

    public CatalogDirectory WithFade(Dictionary<string, Dictionary<string, double[]>> fade)
    {
        Write("fade.json", fade);
        return this;
    }

    // Copies a catalog that ships with the app (resolved from the test working directory, where the
    // build drops it) so a test can assert against real data rather than a stub.
    public CatalogDirectory WithShippedCatalog(string fileName)
    {
        File.Copy(fileName, Path.Combine(_directory, fileName), overwrite: true);
        return this;
    }

    public ConstDataService Build() => new(_directory);

    private void Write<T>(string fileName, T value)
        => File.WriteAllText(Path.Combine(_directory, fileName), JsonSerializer.Serialize(value));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone - nothing to clean up.
        }
    }
}
