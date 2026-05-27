using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copilot.Sw.Config;
using Copilot.Sw.Extensions;
using Copilot.Sw.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Copilot.Sw.ViewModels;

public partial class SettingsWindowViewModel : ObservableObject
{
    #region Fields
    private const string GitHubConfigName = "GitHub Copilot";

    private static readonly string[] FallbackModelPresets = new[]
    {
        "openai/gpt-4o-mini",
        "openai/gpt-4o",
        "openai/gpt-4.1-mini",
        "openai/gpt-4.1",
        "openai/o4-mini",
        "meta/Meta-Llama-3.1-70B-Instruct",
        "mistral-ai/Mistral-Large-2411",
    };

    [ObservableProperty]
    private string _gitHubModel = "openai/gpt-4o-mini";

    [ObservableProperty]
    private string? _gitHubStatus;

    [ObservableProperty]
    private bool _gitHubStatusIsError;

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    private bool _isAwaitingDeviceCode;

    [ObservableProperty]
    private string? _deviceUserCode;

    [ObservableProperty]
    private string? _deviceVerificationUri;

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private string? _signedInIdentity;

    private readonly ITextCompletionProvider _textCompletionProvider;
    private CancellationTokenSource? _signInCts;
    #endregion

    #region Ctor
    public SettingsWindowViewModel(ITextCompletionProvider textCompletionProvider)
    {
        _textCompletionProvider = textCompletionProvider;
        TextCompletionConfigs =
            _textCompletionProvider.Load()?.Select(ToUI).ToObservableCollection() ??
            new ObservableCollection<UITextCompletionConfig>();

        ResetModelPresetsToFallback();

        var existing = TextCompletionConfigs.FirstOrDefault();
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(existing.Model))
            {
                GitHubModel = existing.Model!;
                if (!GitHubModelPresets.Contains(existing.Model!))
                {
                    GitHubModelPresets.Insert(0, existing.Model!);
                }
            }

            if (!string.IsNullOrWhiteSpace(existing.Apikey))
            {
                IsSignedIn = true;
                SignedInIdentity = "Signed in with GitHub.";
                GitHubStatus = existing.IsDefault
                    ? "A GitHub Copilot connection is already saved (default)."
                    : "A GitHub Copilot connection is already saved.";

                // Best-effort refresh of identity + catalog using the saved
                // token. Fire-and-forget on the thread pool so it doesn't
                // block the window from opening.
                _ = RefreshFromGitHubAsync(existing.Apikey!);
            }
        }
    }
    #endregion

    #region Properties
    /// <summary>
    /// Persisted GitHub Models connections. Always 0 or 1 entry, but kept
    /// as a collection so the JSON storage format stays unchanged.
    /// </summary>
    public ObservableCollection<UITextCompletionConfig> TextCompletionConfigs { get; }

    /// <summary>
    /// Models available to the signed-in token. Populated from the GitHub
    /// Models catalog after sign-in; falls back to a hard-coded list
    /// when offline.
    /// </summary>
    public ObservableCollection<string> GitHubModelPresets { get; } = new();
    #endregion

    #region Inline validation
    partial void OnGitHubModelChanged(string value)
    {
        if (!IsSigningIn)
        {
            GitHubStatusIsError = false;
        }
    }
    #endregion

    #region Commands - dialog
    [RelayCommand]
    private void Ok(Window window)
    {
        // Persist model edits even if the user didn't sign in this session.
        if (TextCompletionConfigs.Count > 0)
        {
            var entry = TextCompletionConfigs[0];
            if (!string.IsNullOrWhiteSpace(GitHubModel))
            {
                entry.Model = GitHubModel;
            }
            Save();
        }

        window.DialogResult = true;
    }
    #endregion

    #region Commands - GitHub OAuth device flow
    [RelayCommand]
    private async Task SignInWithGitHubAsync()
    {
        if (!GitHubOAuth.IsConfigured)
        {
            GitHubStatusIsError = true;
            GitHubStatus = "GitHub OAuth is not configured.";
            return;
        }

        _signInCts?.Cancel();
        _signInCts = new CancellationTokenSource();
        var ct = _signInCts.Token;

        IsSigningIn = true;
        GitHubStatusIsError = false;
        GitHubStatus = "Contacting GitHub…";

        try
        {
            var oauth = new GitHubOAuth();
            var deviceCode = await oauth.RequestDeviceCodeAsync(ct).ConfigureAwait(true);

            DeviceUserCode = deviceCode.UserCode;
            DeviceVerificationUri = deviceCode.VerificationUri.ToString();
            IsAwaitingDeviceCode = true;
            GitHubStatus =
                $"Enter the code at {deviceCode.VerificationUri} and approve sign-in.";

            TryOpenBrowser(deviceCode.VerificationUri.ToString());

            var token = await oauth.PollForAccessTokenAsync(deviceCode, ct).ConfigureAwait(true);

            // Fetch identity + catalog before we close the awaiting UI so
            // the model picker is populated by the time the user sees it.
            var login = await oauth.GetUserLoginAsync(token, ct).ConfigureAwait(true);
            var models = await oauth.ListAvailableModelsAsync(token, ct).ConfigureAwait(true);

            ApplyModelList(models, preserveSelection: GitHubModel);
            UpsertConfig(token);
            Save();

            IsSignedIn = true;
            SignedInIdentity = string.IsNullOrEmpty(login)
                ? "Signed in with GitHub."
                : $"Signed in as @{login}.";
            GitHubStatus = $"Signed in. Using model {GitHubModel} (set as default).";
        }
        catch (OperationCanceledException)
        {
            GitHubStatusIsError = true;
            GitHubStatus = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            GitHubStatusIsError = true;
            GitHubStatus = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            IsAwaitingDeviceCode = false;
            DeviceUserCode = null;
            DeviceVerificationUri = null;
        }
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        _signInCts?.Cancel();
    }

    [RelayCommand]
    private void CopyDeviceCode()
    {
        if (!string.IsNullOrEmpty(DeviceUserCode))
        {
            try
            {
                Clipboard.SetText(DeviceUserCode!);
            }
            catch
            {
                // Clipboard can transiently fail; non-fatal.
            }
        }
    }

    [RelayCommand]
    private void OpenVerificationUri()
    {
        if (!string.IsNullOrEmpty(DeviceVerificationUri))
        {
            TryOpenBrowser(DeviceVerificationUri!);
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        TextCompletionConfigs.Clear();
        Save();
        IsSignedIn = false;
        SignedInIdentity = null;
        GitHubStatus = "Signed out.";
        GitHubStatusIsError = false;
    }
    #endregion

    #region Helpers
    private void UpsertConfig(string token)
    {
        TextCompletionConfigs.Clear();
        TextCompletionConfigs.Add(new UITextCompletionConfig
        {
            Name = GitHubConfigName,
            Type = ServerType.GitHubModels,
            Endpoint = TextCompletionConfig.GitHubModelsDefaultEndpoint,
            Model = GitHubModel,
            Apikey = token,
            IsDefault = true,
        });
    }

    private void ResetModelPresetsToFallback()
    {
        GitHubModelPresets.Clear();
        foreach (var id in FallbackModelPresets)
        {
            GitHubModelPresets.Add(id);
        }
    }

    private void ApplyModelList(IReadOnlyList<string> models, string? preserveSelection)
    {
        if (models is null || models.Count == 0)
        {
            // Catalog unreachable — keep the fallback list and the current
            // selection. The user can still type a model id.
            return;
        }

        GitHubModelPresets.Clear();
        foreach (var id in models)
        {
            GitHubModelPresets.Add(id);
        }

        if (!string.IsNullOrWhiteSpace(preserveSelection)
            && !GitHubModelPresets.Contains(preserveSelection!))
        {
            // Keep whatever the user previously chose visible even if the
            // catalog no longer lists it; they might still want to use it.
            GitHubModelPresets.Insert(0, preserveSelection!);
        }

        if (string.IsNullOrWhiteSpace(GitHubModel)
            || !GitHubModelPresets.Contains(GitHubModel))
        {
            GitHubModel = GitHubModelPresets[0];
        }
    }

    private async Task RefreshFromGitHubAsync(string token)
    {
        try
        {
            var oauth = new GitHubOAuth();
            var login = await oauth.GetUserLoginAsync(token).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(login))
            {
                SignedInIdentity = $"Signed in as @{login}.";
            }

            var models = await oauth.ListAvailableModelsAsync(token).ConfigureAwait(true);
            ApplyModelList(models, preserveSelection: GitHubModel);
        }
        catch
        {
            // Non-fatal; the UI continues to work with whatever's loaded.
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal; the verification URI is shown in the UI.
        }
    }

    private static UITextCompletionConfig ToUI(TextCompletionConfig t)
    {
        return new UITextCompletionConfig
        {
            Model = t.Model,
            Endpoint = t.Endpoint,
            Name = t.Name,
            Type = t.Type,
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
