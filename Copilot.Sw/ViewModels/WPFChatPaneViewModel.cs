using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copilot.Sw.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using Copilot.Sw.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Copilot.Sw.Config;
using Copilot.Sw.Skills;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Copilot.Sw.Extensions;

namespace Copilot.Sw.ViewModels;

public partial class WPFChatPaneViewModel : ObservableObject
{
    #region Fields
    private AsyncRelayCommand? _sendCommand;
    private string? _question;
    private readonly ILogger? _logger;
    private readonly IAddin _addin;
    private readonly ITextCompletionProvider _textCompletionProvider;
    protected readonly ISkillsProvider _skillsProvider;
    protected bool _configLoadResult;
    private LocalSemanticFunctionModel _selectedSkill;
    private bool _suppressModelSwitch;
    [ObservableProperty] private bool _isInitializing;
    [ObservableProperty] private string? _selectedModel;
    #endregion

    #region Ctor
    public WPFChatPaneViewModel(
        IAddin addin,
        ITextCompletionProvider textCompletionProvider,
        ISkillsProvider skillsProvider)
    {
        _addin = addin;
        _textCompletionProvider = textCompletionProvider;
        _skillsProvider = skillsProvider;
        Skills = _skillsProvider.GetSkills()
            .SelectMany(p => p.SemanticFunctions)
            .ToList();
        //_logger = logger;
    }
    #endregion

    #region Properties
    public string? Question
    {
        get => _question; set
        {
            SetProperty(ref _question, value);
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    public List<LocalSemanticFunctionModel> Skills { get; set; }

    public LocalSemanticFunctionModel SelectedSkill { get; set; }

    public Conversation Conversation { get; set; } = new();

    public Kernel? Kernel { get; private set; }

    public bool HasItem => Conversation?.Messages?.Any() == true;

    /// <summary>True once an AI provider has been configured and a kernel built.</summary>
    public bool IsConfigured => _configLoadResult;

    /// <summary>Model id of the default chat-completion service, for display in the header.</summary>
    public string? CurrentModel { get; private set; }

    /// <summary>
    /// Catalog of models the signed-in token can use. Populated after sign-in
    /// and refreshed on demand. Drives the inline model picker in the chat pane.
    /// </summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    public AsyncRelayCommand SendCommand { get => _sendCommand ??= new AsyncRelayCommand(SendAsync, CanSend); }
    #endregion

    #region Public Methods
    public void Init()
    {
        BuildKernel();
    }

    /// <summary>
    /// Builds the Semantic Kernel on a background thread so the SolidWorks
    /// UI thread isn't blocked by disk I/O / HTTP handshakes. Any failure
    /// is surfaced as an error message in the chat conversation instead of
    /// throwing into the caller (which historically popped a MessageBox
    /// during add-in startup).
    /// </summary>
    public async Task InitAsync()
    {
        if (IsInitializing)
        {
            return;
        }

        IsInitializing = true;
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    BuildKernel();
                }
                catch (Exception ex)
                {
                    Application.Current?.Dispatcher?.Invoke(() =>
                        Conversation.Messages.Add(
                            Message.CreateError($"Failed to initialise AI provider: {ex.Message}")));
                }
            }).ConfigureAwait(true);

            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(CurrentModel));
            SyncSelectedModel();
            _ = RefreshAvailableModelsAsync();
        }
        finally
        {
            IsInitializing = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private void BuildKernel()
    {
        var configs = _textCompletionProvider.Load();

        if (configs?.Any() != true)
        {
            _configLoadResult = false;
            return;
        }

        var builder = Microsoft.SemanticKernel.Kernel.CreateBuilder();
        var loaded = builder.LoadConfigs(configs);

        if (!loaded)
        {
            _configLoadResult = false;
            return;
        }

        Kernel = builder.Build();
        _configLoadResult = true;
        CurrentModel = configs.FirstOrDefault(c => c.IsDefault)?.Model
            ?? configs.FirstOrDefault()?.Model;
    }
    #endregion

    #region Private Methods
    [RelayCommand]
    private void Clear()
    {
        Question = "";
    }

    [RelayCommand]
    private void ClearConversation()
    {
        Conversation.Clear();
        OnPropertyChanged(nameof(HasItem));
    }

    [RelayCommand]
    private async Task UsePromptAsync(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        Question = prompt;
        if (SendCommand.CanExecute(null))
        {
            await SendCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    protected void OpenSettings()
    {
        try
        {
            var viewModel = _addin.Services.GetService<SettingsWindowViewModel>();
            if (viewModel == null)
            {
                throw new NullReferenceException();
            }

            var settingWindow = new SettingsWindow() { DataContext = viewModel };
            if (settingWindow.ShowDialog() == true)
            {
                settingWindow.Save();
            }

            BuildKernel();
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(CurrentModel));
            SyncSelectedModel();
            _ = RefreshAvailableModelsAsync();
            SendCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
        }
    }

    private bool CanSend() => !string.IsNullOrEmpty(Question) && !IsInitializing;

    partial void OnIsInitializingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
    }

    protected virtual async Task SendAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_question))
        {
            return;
        }

        OnPropertyChanged(nameof(HasItem));

        try
        {
            //check config — prompt the user if no provider is configured
            if (!_configLoadResult)
            {
                OpenSettings();
            }

            if (Kernel == null)
            {
                return;
            }

            await Conversation.ChatAsync(
                Kernel,
                _skillsProvider,
                _question,
                cancellationToken);

            //clear
            Question = "";
        }
        catch (OperationCanceledException)
        {
            // User pressed Stop — Conversation already kept the partial reply.
        }
        catch (Exception ex)
        {
            if (IsUnauthorized(ex))
            {
                // Token rejected by GitHub Models — surface the welcome
                // / sign-in state again so the user can re-authenticate.
                Kernel = null;
                _configLoadResult = false;
                OnPropertyChanged(nameof(IsConfigured));
                Conversation.Messages.Add(Message.CreateError(
                    "Your GitHub sign-in has expired or is missing access to this model. " +
                    "Open Settings and sign in again."));
            }
            else if (IsUnknownModel(ex, out var modelName))
            {
                Conversation.Messages.Add(Message.CreateError(
                    string.IsNullOrEmpty(modelName)
                        ? "The selected model isn't available on GitHub Models with your token. Pick a different model from the picker."
                        : $"\"{modelName}\" isn't available on GitHub Models with your token. Pick a different model from the picker."));
            }
            else
            {
                Conversation.Messages.Add(Message.CreateError(ex.Message));
            }
        }
        finally
        {
            OnPropertyChanged(nameof(HasItem));
        }
    }

    private static bool IsUnauthorized(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var msg = current.Message ?? string.Empty;
            if (msg.Contains("401") ||
                msg.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("invalid_token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("Bad credentials", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsUnknownModel(Exception ex, out string? modelName)
    {
        modelName = null;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var msg = current.Message ?? string.Empty;
            if (msg.IndexOf("unknown_model", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("Unknown model", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var idx = msg.IndexOf("Unknown model", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var tail = msg.Substring(idx);
                    var colon = tail.IndexOf(':');
                    if (colon >= 0)
                    {
                        modelName = tail.Substring(colon + 1).Trim().TrimEnd('.', ')', ',', ' ');
                    }
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Persist the current configs and rebuild the kernel after a property
    /// change like the model selection. Surfaces failures into the chat
    /// rather than throwing.
    /// </summary>
    private void PersistAndRebuild()
    {
        try
        {
            var configs = _textCompletionProvider.Load();
            if (configs is null || configs.Count == 0)
            {
                return;
            }

            var defaultConfig = configs.FirstOrDefault(c => c.IsDefault) ?? configs[0];
            if (!string.IsNullOrWhiteSpace(SelectedModel) &&
                !string.Equals(defaultConfig.Model, SelectedModel, StringComparison.Ordinal))
            {
                defaultConfig.Model = SelectedModel;
                _textCompletionProvider.Write(configs.ToList());
            }

            BuildKernel();
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(CurrentModel));
        }
        catch (Exception ex)
        {
            Conversation.Messages.Add(Message.CreateError(
                $"Failed to switch model: {ex.Message}"));
            OnPropertyChanged(nameof(HasItem));
        }
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (_suppressModelSwitch || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (string.Equals(value, CurrentModel, StringComparison.Ordinal))
        {
            return;
        }

        PersistAndRebuild();
    }

    /// <summary>
    /// Mirrors <see cref="CurrentModel"/> into <see cref="SelectedModel"/>
    /// (and seeds the picker entries) without re-triggering the switch
    /// logic in <see cref="OnSelectedModelChanged"/>.
    /// </summary>
    private void SyncSelectedModel()
    {
        _suppressModelSwitch = true;
        try
        {
            SelectedModel = CurrentModel;
            if (!string.IsNullOrWhiteSpace(CurrentModel) &&
                !AvailableModels.Contains(CurrentModel!))
            {
                AvailableModels.Insert(0, CurrentModel!);
            }
        }
        finally
        {
            _suppressModelSwitch = false;
        }
    }

    /// <summary>
    /// Refreshes <see cref="AvailableModels"/> from the GitHub Models catalog
    /// using the saved token, on the thread pool. No-op if there is no token.
    /// </summary>
    private async Task RefreshAvailableModelsAsync()
    {
        try
        {
            var configs = _textCompletionProvider.Load();
            var token = configs?.FirstOrDefault(c => c.IsDefault)?.Apikey
                ?? configs?.FirstOrDefault()?.Apikey;
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var oauth = new GitHubOAuth();
            var models = await oauth
                .ListAvailableModelsAsync(token!)
                .ConfigureAwait(true);

            if (models is null || models.Count == 0)
            {
                return;
            }

            _suppressModelSwitch = true;
            try
            {
                AvailableModels.Clear();
                foreach (var id in models)
                {
                    AvailableModels.Add(id);
                }

                if (!string.IsNullOrWhiteSpace(CurrentModel) &&
                    !AvailableModels.Contains(CurrentModel!))
                {
                    AvailableModels.Insert(0, CurrentModel!);
                }

                SelectedModel = CurrentModel;
            }
            finally
            {
                _suppressModelSwitch = false;
            }
        }
        catch
        {
            // Non-fatal; the picker keeps whatever was already there.
        }
    }
    #endregion
}