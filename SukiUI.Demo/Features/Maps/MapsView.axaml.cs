using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SukiUI.Demo.Features.Maps;

public partial class MapsView : UserControl
{
    public MapsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
} 