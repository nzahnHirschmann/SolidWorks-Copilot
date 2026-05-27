using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copilot.Sw.Config;
using Copilot.Sw.Extensions;
using Copilot.Sw.Models;
using MvvmDialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Copilot.Sw.ViewModels;

public partial class SettingsWindowViewModel :
    ObservableObject
{
    #region Fields
    private const string GitHubConfigName = "GitHub Copilot";
    private static readonly HttpClient s_githubClient = new HttpClient();

    [ObservableProperty]
    private UITextCompletionConfig? _selectedTextCompletionConfig;

    [ObservableProperty]
    private string? _gitHubToken;

    [ObservableProperty]
    private string _gitHubModel = "openai/gpt-4o-mini";

    [ObservableProperty]
    private string? _gitHubStatus;

    [ObservableProperty]
    private bool _gitHubStatusIsError;

    [ObservableProperty]
    private bool _isSigningIn;

    private ITextCompletionProvider _textCompletionProvider;
    #endregion

    #region Ctor
    public SettingsWindowViewModel(
        ITextCompletionProvider textCompletionProvider)
    {
        _textCompletionProvider = textCompletionProvider;
        TextCompletionConfigs =
            _textCompletionProvider.Load()?.Select(t => ToUI(t))?.ToObservableCollection() ??
            new ObservableCollection<UITextCompletionConfig>();

        // Pre-fill the GitHub fields from an existing GitHub Models entry so
        // returning users see their current model selection.
        var existing = TextCompletionConfigs.FirstOrDefault(c => c.Type == ServerType.GitHubModels);
        if (existing is not null)
        {
            GitHubToken = existing.Apikey;
            if (!string.IsNullOrWhiteSpace(existing.Model))
            {
                GitHubModel = existing.Model!;
            }

            GitHubStatus = existing.IsDefault
                ? "A GitHub Copilot connection is already saved (default)."
                : "A GitHub Copilot connection is already saved.";
        }
    }
    #endregion

    #region Properties
    public ObservableCollection<UITextCompletionConfig> TextCompletionConfigs { get; private set; }

    /// <summary>
    /// Suggested GitHub Models catalog ids. The user can pick one or type
    /// a custom id in the editable ComboBox.
    /// </summary>
    public IReadOnlyList<string> GitHubModelPresets { get; } = new[]
    {
        "openai/gpt-4o-mini",
        "openai/gpt-4o",
        "openai/gpt-4.1-mini",
        "openai/gpt-4.1",
        "openai/o4-mini",
        "meta/Meta-Llama-3.1-70B-Instruct",
        "mistral-ai/Mistral-Large-2411",
    };
    #endregion

    #region Commands - General
    [RelayCommand]
    private void Ok(Window window)
    {
        window.DialogResult = true;
    }

    [RelayCommand]
    private void Add()
    {
        bool nothing = TextCompletionConfigs.Count == 0;
        TextCompletionConfigs.Add(new UITextCompletionConfig()
        {
            Name = "GitHub Models",
            Model = "openai/gpt-4o-mini",
            Type = ServerType.GitHubModels,
            Endpoint = GitHubModelsTextCompletion.DefaultEndpoint,
            IsDefault = nothing,
        });
    }

    [RelayCommand]
    private void Delete()
    {
        if (_selectedTextCompletionConfig == null)
        {
            return;
        }

        TextCompletionConfigs.Remove(_selectedTextCompletionConfig);
    }

    [RelayCommand]
    private void SetAsDefault()
    {
        if (_selectedTextCompletionConfig == null)
        {
            return;
        }

        foreach (var config in TextCompletionConfigs)
        {
            config.IsDefault = false;
        }

        SelectedTextCompletionConfig!.IsDefault = true;
    }
    #endregion

    #region Commands - GitHub one-click sign-in
    /// <summary>
    /// Opens the GitHub personal access token creation page in the default
    /// browser with the required scope and description pre-filled.
    /// </summary>
    [RelayCommand]
    private void OpenGitHubTokenPage()
    {
        // Classic PAT with read:org is the simplest scope that unlocks
        // GitHub Models for individual users. Fine-grained tokens with
        // "Models: read" also work; users can paste either kind.
        const string url =
            "https://github.com/settings/tokens/new" +
            "?scopes=read:org" +
            "&description=SolidWorks%20Copilot%20-%20GitHub%20Models";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open browser: {ex.Message}");
        }
    }

    /// <summary>
    /// One-click sign-in: validates the pasted GitHub token by calling
    /// <c>GET https://api.github.com/user</c>, then creates or updates the
    /// <see cref="ServerType.GitHubModels"/> entry and marks it as default.
    /// </summary>
    [RelayCommand]
    private async Task SignInWithGitHubAsync()
    {
        if (string.IsNullOrWhiteSpace(GitHubToken))
        {
            GitHubStatusIsError = true;
            GitHubStatus = "Paste a GitHub token first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(GitHubModel))
        {
            GitHubStatusIsError = true;
            GitHubStatus = "Pick or type a model id (e.g. openai/gpt-4o-mini).";
            return;
        }

        IsSigningIn = true;
        GitHubStatusIsError = false;
        GitHubStatus = "Verifying token with GitHub…";

        try
        {
            var login = await VerifyGitHubTokenAsync(GitHubToken!).ConfigureAwait(true);

            // Upsert: replace any existing GitHub Models entry by name.
            var existing = TextCompletionConfigs
                .FirstOrDefault(c => c.Type == ServerType.GitHubModels
                                     && string.Equals(c.Name, GitHubConfigName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                TextCompletionConfigs.Remove(existing);
            }

            var entry = new UITextCompletionConfig
            {
                Name = GitHubConfigName,
                Type = ServerType.GitHubModels,
                Endpoint = GitHubModelsTextCompletion.DefaultEndpoint,
                Model = GitHubModel,
                Apikey = GitHubToken,
                IsDefault = true,
            };

            foreach (var c in TextCompletionConfigs)
            {
                c.IsDefault = false;
            }
            TextCompletionConfigs.Add(entry);

            // Persist immediately so the chat pane picks it up on the next
            // BuildKernel() without forcing the user to also click "Ok".
            Save();

            GitHubStatus = string.IsNullOrEmpty(login)
                ? $"Signed in. Using model {GitHubModel} (set as default)."
                : $"Signed in as @{login}. Using model {GitHubModel} (set as default).";
        }
        catch (Exception ex)
        {
            GitHubStatusIsError = true;
            GitHubStatus = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
        }
    }
    #endregion

    #region Helpers
    private static async Task<string?> VerifyGitHubTokenAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("SolidWorks-Copilot");

        using var response = await s_githubClient.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub rejected the token ({(int)response.StatusCode} {response.ReasonPhrase}). " +
                "Make sure it has the 'read:org' scope (classic) or 'Models: read' permission (fine-grained).");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("login", out var loginProp)
                && loginProp.ValueKind == JsonValueKind.String)
            {
                return loginProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-fatal: token is valid, we just don't have the login name.
        }

        return null;
    }

    private UITextCompletionConfig ToUI(TextCompletionConfig t)
    {
        return new UITextCompletionConfig()
        {
            Model = t.Model,
            Endpoint = t.Endpoint,
            Name = t.Name,
            Type = t.Type,
            Org = t.Org,
            Apikey = t.Apikey,
            IsDefault = t.IsDefault,
        };
    }

    internal void Save()
    {
        _textCompletionProvider.Write(
            TextCompletionConfigs
            .Select(p => p.ToTextCompletionConfig())
            .ToList());
    }
    #endregion
}
