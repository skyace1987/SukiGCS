using Material.Icons;
using SukiUI.Demo.Features;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SukiUI.Demo.Features.Maps;

public partial class MapsViewModel : DemoPageBase
{
    public string Token { get; } = "1770717c0fbaee5a9e95519ef34c6df2";  // 请替换为实际的Token

    public MapsViewModel() : base("地图", MaterialIconKind.Map, int.MinValue)
    {
    }
} 