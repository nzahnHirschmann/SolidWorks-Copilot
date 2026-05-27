using CommunityToolkit.Mvvm.ComponentModel;
using Copilot.Sw.Config;

namespace Copilot.Sw.Models;

/// <summary>
/// MVVM-friendly view of <see cref="TextCompletionConfig"/>. Only represents
/// a GitHub Models connection.
/// </summary>
public sealed partial class UITextCompletionConfig : ObservableObject
{
    /// <summary>Friendly name for this saved connection.</summary>
    [ObservableProperty] public string? _name;

    /// <summary>Provider kind. Always <see cref="ServerType.GitHubModels"/>.</summary>
    [ObservableProperty] public ServerType _type = ServerType.GitHubModels;

    /// <summary>GitHub Models model id.</summary>
    [ObservableProperty] public string? _model;

    /// <summary>Inference endpoint base URL.</summary>
    [ObservableProperty] public string? _endpoint;

    /// <summary>GitHub OAuth token (encrypted at rest).</summary>
    [ObservableProperty] public string? _apikey;

    /// <summary>Whether this entry is the kernel's default chat service.</summary>
    [ObservableProperty] private bool _isDefault;

    public override string ToString()
    {
        var str = $"{Type}:{Name}";
        if (!string.IsNullOrEmpty(Endpoint))
        {
            str += $"({Endpoint})";
        }
        return str;
    }

    public TextCompletionConfig ToTextCompletionConfig()
    {
        return new TextCompletionConfig()
        {
            Model = Model,
            Endpoint = Endpoint,
            Name = Name,
            Type = Type,
            Apikey = Apikey,
            IsDefault = IsDefault,
        };
    }
}
