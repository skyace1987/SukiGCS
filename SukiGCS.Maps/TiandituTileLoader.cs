using System.Diagnostics;
using System.Net.Http;
using Avalonia.Media.Imaging;
using System.IO;

namespace SukiGCS.Maps;

public class TiandituTileLoader
{
    private readonly string _token;
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://t{0}.tianditu.gov.cn/img_w/wmts";
    private static readonly Dictionary<string, byte[]> _memoryCache = new();

    public TiandituTileLoader(string token)
    {
        _token = token;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.tianditu.gov.cn/");
        Debug.WriteLine($"TiandituTileLoader initialized with token: {token}");
    }

    public async Task<Bitmap?> GetTileAsync(int x, int y, int zoom)
    {
        try
        {
            // 验证参数
            if (string.IsNullOrEmpty(_token))
            {
                Debug.WriteLine("Token is empty");
                return null;
            }

            if (zoom < 1 || zoom > 18)
            {
                Debug.WriteLine($"Invalid zoom level: {zoom}");
                return null;
            }

            // 计算该级别下的瓦片范围
            var maxTile = 1 << zoom;
            if (y < 0 || y >= maxTile)
            {
                Debug.WriteLine($"Invalid y coordinate: {y}, max is {maxTile - 1}");
                return null;
            }

            // 规范化 x 坐标
            x = ((x % maxTile) + maxTile) % maxTile;

            var cacheKey = $"{x}_{y}_{zoom}";
            
            // 检查内存缓存
            if (_memoryCache.TryGetValue(cacheKey, out var cachedData))
            {
                Debug.WriteLine($"Cache hit for tile {cacheKey}");
                using var cacheStream = new MemoryStream(cachedData);
                return new Bitmap(cacheStream);
            }

            // 使用不同的服务器节点
            var server = (x + y) % 8;
            var url = string.Format(BaseUrl, server) + 
                     $"?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER=img&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles" +
                     $"&TILEMATRIX={zoom}&TILEROW={y}&TILECOL={x}&tk={_token}";
            
            Debug.WriteLine($"Requesting tile: x={x}, y={y}, zoom={zoom}, server={server}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "image/webp,image/apng,image/*,*/*;q=0.8");
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            request.Headers.Add("Connection", "keep-alive");
            
            using var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"Failed to load tile: {response.StatusCode}");
                Debug.WriteLine($"Error content: {errorContent}");
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync();
            if (data.Length == 0)
            {
                Debug.WriteLine("Received empty data");
                return null;
            }

            // 缓存数据
            _memoryCache[cacheKey] = data;

            using var tileStream = new MemoryStream(data);
            var bitmap = new Bitmap(tileStream);
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading tile: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Debug.WriteLine($"Inner error: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            return null;
        }
    }
} 