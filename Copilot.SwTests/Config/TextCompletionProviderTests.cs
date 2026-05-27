using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Copilot.Sw.Config.Tests;

[TestClass]
public class TextCompletionProviderTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "SolidWorks-Copilot-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private TextCompletionProvider NewProvider() => new() { SaveLocation = _tempDir };

    [TestMethod]
    public void Constructor_Defaults_To_AppData_Subfolder()
    {
        var provider = new TextCompletionProvider();
        Assert.IsNotNull(provider);
        StringAssert.Contains(provider.SaveLocation, "SolidWorks Copilot");
    }

    [TestMethod]
    public void Load_When_File_Missing_Returns_Null()
    {
        var provider = NewProvider();
        Assert.IsNull(provider.Load());
    }

    [TestMethod]
    public void Write_Then_Load_RoundTrips_Basic_Fields()
    {
        var provider = NewProvider();

        provider.Write(new List<TextCompletionConfig>
        {
            new()
            {
                Name = "primary",
                Type = ServerType.GitHubModels,
                Model = "gpt-4o-mini",
                Apikey = "sk-fake-test-key-1234567890",
                IsDefault = true,
            }
        });

        var loaded = provider.Load();
        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded!.Count);

        var c = loaded[0];
        Assert.AreEqual("primary", c.Name);
        Assert.AreEqual(ServerType.GitHubModels, c.Type);
        Assert.AreEqual("gpt-4o-mini", c.Model);
        Assert.AreEqual("sk-fake-test-key-1234567890", c.Apikey);
        Assert.IsTrue(c.IsDefault);
    }

    [TestMethod]
    public void Apikey_Is_Encrypted_On_Disk()
    {
        var provider = NewProvider();

        const string secret = "sk-this-must-not-appear-in-the-file";
        provider.Write(new List<TextCompletionConfig>
        {
            new() { Name = "x", Type = ServerType.GitHubModels, Apikey = secret }
        });

        var raw = File.ReadAllText(provider.FilePathName);
        Assert.IsFalse(raw.Contains(secret), "API key was persisted in plaintext.");
        StringAssert.Contains(raw.ToLowerInvariant(), "dpapi:");
    }

    [TestMethod]
    public void Plaintext_Legacy_Files_Are_Still_Readable()
    {
        var provider = NewProvider();

        // Simulate a pre-encryption settings file written by an older build.
        var json = System.Text.Json.JsonSerializer.Serialize(new List<TextCompletionConfig>
        {
            new() { Name = "old", Type = ServerType.GitHubModels, Apikey = "legacy-plain" }
        });
        File.WriteAllText(provider.FilePathName, json);

        var loaded = provider.Load();
        Assert.IsNotNull(loaded);
        Assert.AreEqual("legacy-plain", loaded![0].Apikey);
    }
}