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
    [ObservableProperty] private bool _isInitializing;
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
    #endregion
}