using SkiaSharp;

namespace Core {
    public class BackgroundMapManager {
        // Каталог для кэша тайлов карты
        private static readonly string tileCacheDirectory = "tile_cache";

        private readonly Dictionary<(int zoom, int xTileMin, int xTileMax, int yTileMin, int yTileMax), SKBitmap> _cache = [];
        private readonly Dictionary<(int imageWidth, double minLon, double maxLon), int> _zoomLevelCache = [];

        public SKBitmap GetBackgroundMap(int zoom, int xTileMin, int xTileMax, int yTileMin, int yTileMax) {
            (int zoom, int xTileMin, int xTileMax, int yTileMin, int yTileMax) key = (zoom, xTileMin, xTileMax, yTileMin, yTileMax);

            if(!_cache.TryGetValue(key, out SKBitmap? backgroundMap)) {
                backgroundMap = CreateBackgroundMap(zoom, xTileMin, xTileMax, yTileMin, yTileMax);
                _cache[key] = backgroundMap;
            }

            return backgroundMap;
        }

        // Кэширование уровня зума
        public int GetZoomLevel(double minLon, double maxLon, int imageWidth) {
            (int imageWidth, double minLon, double maxLon) key = (imageWidth, minLon, maxLon);

            if(!_zoomLevelCache.TryGetValue(key, out int zoomLevel)) {
                zoomLevel = CalculateZoomLevel(minLon, maxLon, imageWidth);
                _zoomLevelCache[key] = zoomLevel;
            }

            return zoomLevel;
        }

        // Функция для создания фоновой карты
        private static SKBitmap CreateBackgroundMap(int zoom, int xTileMin, int xTileMax, int yTileMin, int yTileMax) {
            int tileWidth = 256;
            int tileHeight = 256;

            int numTilesX = xTileMax - xTileMin + 1;
            int numTilesY = yTileMax - yTileMin + 1;

            int backgroundWidth = numTilesX * tileWidth;
            int backgroundHeight = numTilesY * tileHeight;

            var backgroundBitmap = new SKBitmap(backgroundWidth, backgroundHeight);

            Directory.CreateDirectory(tileCacheDirectory);

            using(var tileCanvas = new SKCanvas(backgroundBitmap)) {
                for(int x = xTileMin; x <= xTileMax; x++) {
                    for(int y = yTileMin; y <= yTileMax; y++) {
                        SKBitmap? tileBitmap = GetTile(zoom, x, y) ?? throw new ArgumentException("ArgumentException - CreateBackgroundMap");

                        int offsetX = (x - xTileMin) * tileWidth;
                        int offsetY = (y - yTileMin) * tileHeight;

                        tileCanvas.DrawBitmap(tileBitmap, offsetX, offsetY);
                    }
                }
            }

            return backgroundBitmap;
        }

        // Функция для получения тайла карты
        private static SKBitmap? GetTile(int zoom, int xTile, int yTile) {
            string tilePath = Path.Combine(tileCacheDirectory, zoom.ToString(), xTile.ToString(), yTile.ToString() + ".png");

            if(File.Exists(tilePath)) {
                // Загрузка тайла из кэша
                using FileStream stream = File.OpenRead(tilePath);
                return SKBitmap.Decode(stream);
            } else {
                // Скачивание тайла
                string url = string.Format("https://tile.openstreetmap.org/{0}/{1}/{2}.png", zoom, xTile, yTile);

                Console.WriteLine($"Загрузка тайла: {zoom} {xTile} {yTile}");

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "GRIBToMap/1.0");
                try {
                    byte[] data = client.GetByteArrayAsync(url).Result;

                    // Сохранение тайла в кэш
                    string? tileDir = Path.GetDirectoryName(tilePath);
                    if(string.IsNullOrEmpty(tileDir))
                        return null;

                    Directory.CreateDirectory(tileDir);
                    File.WriteAllBytes(tilePath, data);

                    // Загрузка тайла
                    using var ms = new MemoryStream(data);
                    return SKBitmap.Decode(ms);
                } catch {
                    // Не удалось скачать тайл
                    return null;
                }
            }
        }

        // Функция для вычисления уровня зума
        private static int CalculateZoomLevel(double minLon, double maxLon, int imageWidth) {
            for(int z = 18; z >= 0; z--) {
                double worldPixelWidth = 256 * Math.Pow(2, z);
                double lonPerPixel = 360.0 / worldPixelWidth;
                double pixelWidth = (maxLon - minLon) / lonPerPixel;
                if(pixelWidth <= imageWidth) return z;
            }

            return 0;
        }
    }
}

