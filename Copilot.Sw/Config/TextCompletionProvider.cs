using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Copilot.Sw.Config;

public class TextCompletionProvider:ITextCompletionProvider
{
    // Values whose stored form starts with this prefix are DPAPI-encrypted
    // (CurrentUser scope) and base64-encoded. Anything else is treated as
    // plaintext for backward compatibility with pre-encryption configs.
    private const string ProtectedPrefix = "dpapi:";

    public string SaveLocation { get; set; }

    public string FilePathName => Path.Combine(SaveLocation, "settings.json");

    public TextCompletionProvider()
    {
        SaveLocation = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AddIn.AddinName);
    }

    public IReadOnlyList<TextCompletionConfig>? Load()
    {
        Check();

        if (!File.Exists(FilePathName))
        {
            return null;
        }

        var text = File.ReadAllText(FilePathName);
        var configs = JsonSerializer.Deserialize<List<TextCompletionConfig>>(text);

        if (configs is not null)
        {
            foreach (var c in configs)
            {
                c.Apikey = Unprotect(c.Apikey);
            }
        }

        return configs;
    }

    public void Write(IList<TextCompletionConfig> textCompletionConfigs)
    {
        Check();

        if (textCompletionConfigs is null)
        {
            throw new ArgumentNullException(nameof(textCompletionConfigs));
        }

        // Serialize a copy with encrypted secrets so the in-memory configs
        // remain usable after the write.
        var toPersist = new List<TextCompletionConfig>(textCompletionConfigs.Count);
        foreach (var c in textCompletionConfigs)
        {
            toPersist.Add(new TextCompletionConfig
            {
                Name = c.Name,
                Type = c.Type,
                Model = c.Model,
                Endpoint = c.Endpoint,
                IsDefault = c.IsDefault,
                Apikey = Protect(c.Apikey),
            });
        }

        var text = JsonSerializer.Serialize(toPersist);

        File.WriteAllText(FilePathName, text);
    }

    private void Check()
    {
        if (!Directory.Exists(SaveLocation))
        {
            Directory.CreateDirectory(SaveLocation);
        }
    }

    private static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return plaintext;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext!);
            var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(cipher);
        }
        catch
        {
            // Fall back to plaintext rather than losing the secret outright.
            return plaintext;
        }
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)
            || !stored!.StartsWith(ProtectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return stored;
        }

        try
        {
            var cipher = Convert.FromBase64String(stored.Substring(ProtectedPrefix.Length));
            var bytes = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Corrupted or copied from another user — surface as empty so
            // the user is forced to re-enter the key rather than crashing.
            return null;
        }
    }
}
