using CommunityToolkit.Mvvm.DependencyInjection;
using Copilot.Sw.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Xarial.XCad.Base.Attributes;

namespace Copilot.Sw;

/// <summary>
/// WPFChatPane.xaml 的交互逻辑
/// </summary>
[Title(AddIn.AddinName)]
[Icon(typeof(Properties.Resources),nameof(Properties.Resources.SolidWorksCopilot))]
public partial class WPFChatPane : UserControl
{
    private WPFChatPaneViewModel _vm;
    private double _lastExtentHeight;

    public WPFChatPane()
    {
        InitializeComponent();
        DataContext = _vm = Ioc.Default.GetService<WPFChatPaneViewModel>();

        Loaded += OnLoaded;
        ChatScroll.ScrollChanged += OnChatScrollChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // Fire-and-forget on the UI thread: InitAsync surfaces any error as
        // a chat message rather than throwing.
        await _vm.InitAsync();
    }

    /// <summary>
    /// Auto-scroll to the newest content whenever the chat grows (new
    /// message added or a streaming reply gets longer), but only if the
    /// user is already near the bottom — don't yank them away from
    /// content they've scrolled up to read.
    /// </summary>
    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange <= 0)
        {
            _lastExtentHeight = e.ExtentHeight;
            return;
        }

        var nearBottom =
            ChatScroll.VerticalOffset + ChatScroll.ViewportHeight
            >= _lastExtentHeight - 40;

        _lastExtentHeight = e.ExtentHeight;

        if (nearBottom)
        {
            ChatScroll.ScrollToEnd();
        }
    }
}
