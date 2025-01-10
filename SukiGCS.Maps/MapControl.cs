using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SukiGCS.Maps;

public class MapControl : Control
{
    private Point _lastPosition;
    private bool _isPanning;
    private double _zoom = 3.0;
    private Point _center = new(116.3, 39.9);
    private TiandituTileLoader? _tileLoader;
    private readonly Dictionary<string, Bitmap?> _tileCache = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _isDirty = false;
    private const int TileSize = 256;
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SukiGCS",
        "MapCache"
    );
    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SukiGCS",
        "config.json"
    );

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<MapControl, IBrush?>(nameof(Background));

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly DirectProperty<MapControl, string?> TokenProperty =
        AvaloniaProperty.RegisterDirect<MapControl, string?>(
            nameof(Token),
            o => o.Token,
            (o, v) => o.Token = v);

    private string? _token;
    public string? Token
    {
        get => _token;
        set
        {
            if (SetAndRaise(TokenProperty, ref _token, value))
            {
                _tileLoader = value != null ? new TiandituTileLoader(value) : null;
                InvalidateVisual();
            }
        }
    }

    private class MapConfig
    {
        public double Zoom { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }

    public MapControl()
    {
        ClipToBounds = true;
        
        // 确保缓存目录存在
        try
        {
            Directory.CreateDirectory(_cachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            LoadConfig();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"创建缓存目录失败: {ex.Message}");
        }

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // 约60fps
        };
        _refreshTimer.Tick += (s, e) =>
        {
            if (_isDirty)
            {
                InvalidateVisual();
                _isDirty = false;
            }
        };
        _refreshTimer.Start();

        // 在控件卸载时保存配置
        this.DetachedFromVisualTree += (s, e) => SaveConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<MapConfig>(json);
                if (config != null)
                {
                    _zoom = config.Zoom;
                    _center = new Point(config.CenterX, config.CenterY);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new MapConfig
            {
                Zoom = _zoom,
                CenterX = _center.X,
                CenterY = _center.Y
            };
            var json = JsonSerializer.Serialize(config);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存配置失败: {ex.Message}");
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isPanning) return;

        var position = e.GetPosition(this);
        var delta = position - _lastPosition;
        
        // 转换屏幕像素到经纬度变化
        var scale = 360.0 / (Math.Pow(2, _zoom) * TileSize);
        _center = new Point(
            _center.X - delta.X * scale,
            _center.Y + delta.Y * scale * Math.Cos(_center.Y * Math.PI / 180)
        );
        
        _lastPosition = position;
        _isDirty = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var position = e.GetPosition(this);
        
        // 保存鼠标位置对应的经纬度
        var scale = 360.0 / (Math.Pow(2, _zoom) * TileSize);
        var dx = (position.X - Bounds.Width / 2) * scale;
        var dy = -(position.Y - Bounds.Height / 2) * scale * Math.Cos(_center.Y * Math.PI / 180);
        var mouseLon = _center.X + dx;
        var mouseLat = _center.Y + dy;

        // 整数级缩放
        _zoom = Math.Round(_zoom);  // 确保当前级别为整数
        _zoom += e.Delta.Y > 0 ? 1 : -1;  // 每次改变一个完整级别
        _zoom = Math.Max(2, Math.Min(_zoom, 18.0));

        // 调整中心点，保持鼠标位置不变
        scale = 360.0 / (Math.Pow(2, _zoom) * TileSize);
        dx = (position.X - Bounds.Width / 2) * scale;
        dy = -(position.Y - Bounds.Height / 2) * scale * Math.Cos(mouseLat * Math.PI / 180);
        _center = new Point(mouseLon - dx, mouseLat - dy);

        _isDirty = true;
    }

    private (double x, double y) WorldToTilePos(double lon, double lat, int zoom)
    {
        var n = Math.Pow(2, zoom);
        var x = ((lon + 180.0) / 360.0 * n);
        var latRad = lat * Math.PI / 180.0;
        var y = ((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);
        return (x, y);
    }

    private (int x, int y) WorldToTile(double lon, double lat, int zoom)
    {
        var pos = WorldToTilePos(lon, lat, zoom);
        return ((int)pos.x, (int)pos.y);
    }

    private void PreloadTiles(int centerX, int centerY, int zoom, int radius)
    {
        var maxTile = 1 << zoom;
        // 预加载当前级别
        for (var x = centerX - radius; x <= centerX + radius; x++)
        {
            for (var y = centerY - radius; y <= centerY + radius; y++)
            {
                if (y < 0 || y >= maxTile) continue;
                var normalizedX = ((x % maxTile) + maxTile) % maxTile;
                var key = $"{normalizedX}_{y}_{zoom}";
                if (!_tileCache.ContainsKey(key))
                {
                    _tileCache[key] = null;
                    LoadTileAsync(normalizedX, y, zoom, key);
                }
            }
        }

        // 预加载下一级别（如果不是最大级别）
        if (zoom < 18)
        {
            var nextZoom = zoom + 1;
            var nextMaxTile = 1 << nextZoom;
            var nextCenterX = centerX * 2;
            var nextCenterY = centerY * 2;
            
            for (var x = nextCenterX - 1; x <= nextCenterX + 2; x++)
            {
                for (var y = nextCenterY - 1; y <= nextCenterY + 2; y++)
                {
                    if (y < 0 || y >= nextMaxTile) continue;
                    var normalizedX = ((x % nextMaxTile) + nextMaxTile) % nextMaxTile;
                    var key = $"{normalizedX}_{y}_{nextZoom}";
                    if (!_tileCache.ContainsKey(key))
                    {
                        _tileCache[key] = null;
                        LoadTileAsync(normalizedX, y, nextZoom, key);
                    }
                }
            }
        }
    }

    private class LowerTileInfo
    {
        public Bitmap Tile { get; set; }
        public int Scale { get; set; }
    }

    private LowerTileInfo? GetLowerZoomTile(int x, int y, int zoom)
    {
        if (zoom <= 2) return null;  // 最小级别没有更低级别的瓦片

        // 从近到远尝试获取低级别的瓦片
        for (var lowerZoom = zoom - 1; lowerZoom >= 2; lowerZoom--)
        {
            var scale = zoom - lowerZoom;  // 级别差
            var lowerX = x >> scale;  // 除以 2^scale
            var lowerY = y >> scale;
            var key = $"{lowerX}_{lowerY}_{lowerZoom}";

            var tile = _tileCache.GetValueOrDefault(key);
            if (tile != null)
            {
                return new LowerTileInfo { Tile = tile, Scale = scale };
            }
        }

        return null;
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _tileLoader == null) return;

        // 绘制背景
        if (Background != null)
        {
            context.FillRectangle(Background, new Rect(Bounds.Size));
        }

        var zoom = (int)_zoom;

        // 计算当前视图范围内的瓦片
        var centerTile = WorldToTile(_center.X, _center.Y, zoom);
        var tilesX = (int)Math.Ceiling(Bounds.Width / TileSize) + 2;
        var tilesY = (int)Math.Ceiling(Bounds.Height / TileSize) + 2;

        var minTileX = centerTile.x - tilesX / 2;
        var maxTileX = centerTile.x + tilesX / 2;
        var minTileY = centerTile.y - tilesY / 2;
        var maxTileY = centerTile.y + tilesY / 2;

        // 计算屏幕中心点对应的像素坐标
        var centerPixel = WorldToTilePos(_center.X, _center.Y, zoom);
        var screenCenterX = Bounds.Width / 2;
        var screenCenterY = Bounds.Height / 2;

        // 计算偏移量
        var offsetX = screenCenterX - centerPixel.x * TileSize;
        var offsetY = screenCenterY - centerPixel.y * TileSize;

        // 创建变换矩阵
        var transform = Matrix.CreateTranslation(offsetX, offsetY);

        // 预加载周围的瓦片
        PreloadTiles(centerTile.x, centerTile.y, zoom, 2);

        using (context.PushTransform(transform))
        {
            for (var x = minTileX; x <= maxTileX; x++)
            {
                for (var y = minTileY; y <= maxTileY; y++)
                {
                    // 检查瓦片是否在有效范围内
                    if (y < 0 || y >= (1 << zoom)) continue;

                    // 处理经度循环
                    var normalizedX = ((x % (1 << zoom)) + (1 << zoom)) % (1 << zoom);

                    var key = $"{normalizedX}_{y}_{zoom}";
                    var tileRect = new Rect(
                        normalizedX * TileSize,
                        y * TileSize,
                        TileSize,
                        TileSize
                    );

                    var tile = _tileCache.GetValueOrDefault(key);
                    if (tile != null)
                    {
                        context.DrawImage(tile, tileRect);
                    }
                    else
                    {
                        // 如果当前级别的瓦片未加载，尝试显示低级别的瓦片
                        var lowerTileInfo = GetLowerZoomTile(normalizedX, y, zoom);
                        if (lowerTileInfo != null)
                        {
                            // 计算低级别瓦片的显示区域
                            var subX = (normalizedX % (1 << lowerTileInfo.Scale)) * (TileSize >> lowerTileInfo.Scale);
                            var subY = (y % (1 << lowerTileInfo.Scale)) * (TileSize >> lowerTileInfo.Scale);
                            var subSize = TileSize >> lowerTileInfo.Scale;
                            var sourceRect = new Rect(subX, subY, subSize, subSize);
                            context.DrawImage(lowerTileInfo.Tile, sourceRect, tileRect);
                        }

                        if (!_tileCache.ContainsKey(key))
                        {
                            _tileCache[key] = null;
                            LoadTileAsync(normalizedX, y, zoom, key);
                        }
                    }
                }
            }
        }
    }

    private string GetCacheFilePath(int x, int y, int zoom)
    {
        return Path.Combine(_cachePath, $"{zoom}", $"{x}", $"{y}.png");
    }

    private async Task<Bitmap?> LoadTileFromCache(int x, int y, int zoom)
    {
        var filePath = GetCacheFilePath(x, y, zoom);
        if (!File.Exists(filePath)) return null;

        try
        {
            using var fileStream = File.OpenRead(filePath);
            return new Bitmap(fileStream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"从缓存加载瓦片失败: {ex.Message}");
            return null;
        }
    }

    private async Task SaveTileToCache(int x, int y, int zoom, Bitmap tile)
    {
        var filePath = GetCacheFilePath(x, y, zoom);
        var directory = Path.GetDirectoryName(filePath);
        
        try
        {
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            using var fileStream = File.Create(filePath);
            tile.Save(fileStream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存瓦片到缓存失败: {ex.Message}");
        }
    }

    private async void LoadTileAsync(int x, int y, int zoom, string key)
    {
        try
        {
            if (_tileLoader == null) return;

            // 首先尝试从本地缓存加载
            var cachedTile = await LoadTileFromCache(x, y, zoom);
            if (cachedTile != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _tileCache[key] = cachedTile;
                    _isDirty = true;
                });
                return;
            }

            // 如果本地没有缓存，则从服务器下载
            var tile = await _tileLoader.GetTileAsync(x, y, zoom);
            if (tile != null)
            {
                await SaveTileToCache(x, y, zoom, tile);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _tileCache[key] = tile;
                    _isDirty = true;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LoadTileAsync error: {ex.Message}");
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _lastPosition = e.GetPosition(this);
        _isPanning = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isPanning = false;
        e.Pointer.Capture(null);
    }
} 