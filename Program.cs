using OSGeo.GDAL;
using OSGeo.OSR;

using SkiaSharp;

class Program {
    // Настройки изображения
    private static readonly int imageWidth = 2048; // Ширина изображения
    private static readonly int imageHeight = 1080; // Высота изображения

    // Количество уровней контуров для большей детализации
    private static readonly int contourLevels = 15;

    // Таблица линий для каждой из 16 возможных конфигураций
    private static readonly int[,] lineTable = new int[16, 4]
    {
        { -1, -1, -1, -1 }, // 0
        { 3, 0, -1, -1 },   // 1
        { 0, 1, -1, -1 },   // 2
        { 3, 1, -1, -1 },   // 3
        { 1, 2, -1, -1 },   // 4
        { 0, 1, 2, 3 },     // 5
        { 0, 2, -1, -1 },   // 6
        { 2, 3, -1, -1 },   // 7
        { 2, 3, -1, -1 },   // 8
        { 0, 2, -1, -1 },   // 9
        { 0, 1, 2, 3 },     // 10
        { 1, 2, -1, -1 },   // 11
        { 1, 3, -1, -1 },   // 12
        { 0, 1, -1, -1 },   // 13
        { 0, 3, -1, -1 },   // 14
        { -1, -1, -1, -1 }  // 15
    };

    // Количество цветов в палитре
    private static readonly int colorPaletteSize = 256;

    // Уровень прозрачности (0 - полностью прозрачный, 255 - полностью непрозрачный)
    private static readonly byte desiredAlpha = 64; // 128 для 50% прозрачности
    private static readonly byte contourAlpha = 128; // 128 для 50% прозрачности

    // Параметры для динамической толщины линии
    private static readonly float baseResolution = 1920f; // Базовая ширина разрешения
    private static readonly float baseStrokeWidth = 1.5f; // Базовая толщина линии при базовом разрешении

    // Малое число для избежания деления на ноль
    private static readonly float epsilon = 1e-7f;

    // Выходной каталог для сохранения карт
    private static readonly string outputDirectory = "output_maps";

    // Каталог для кэша тайлов карты
    private static readonly string tileCacheDirectory = "tile_cache";

    // Шаблон URL тайлов карты (замените на подходящий тайловый сервер)
    private static readonly string tileUrlTemplate = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";

    static void Main() {
        // Инициализация GDAL
        Gdal.AllRegister();

        // Путь к вашему GRIB файлу
        string filePath = "GRIBSOA00.000.1";

        DrawAllMaps(filePath);
    }

    static void DrawAllMaps(string filePath) {
        // Открытие файла GRIB
        using Dataset dataset = Gdal.Open(filePath, Access.GA_ReadOnly);
        if(dataset == null) {
            Console.WriteLine("Не удалось открыть файл GRIB.");
            return;
        }

        int layerCount = dataset.RasterCount;
        Console.WriteLine($"Файл содержит {layerCount} слоев.");

        // Создание директории для сохранения карт
        Directory.CreateDirectory(outputDirectory);

        // Последовательная обработка каждого слоя
        for(int i = 1; i <= layerCount; i++) {
            using Band band = dataset.GetRasterBand(i);
            DrawLayerWithSmoothContours(dataset, band, outputDirectory, i);
        }

        Console.WriteLine($"Все карты успешно сохранены в {outputDirectory}");
    }

    static void DrawLayerWithSmoothContours(Dataset dataset, Band band, string outputDirectory, int layerIndex) {
        int width = band.XSize;
        int height = band.YSize;

        // Чтение данных из слоя
        float[] data = new float[width * height];
        band.ReadRaster(0, 0, width, height, data, width, height, 0, 0);

        // Получение геотрансформации и преобразования координат
        double[] geoTransform = new double[6];
        dataset.GetGeoTransform(geoTransform);

        var srcSpatialRef = new SpatialReference(dataset.GetProjectionRef());
        var dstSpatialRef = new SpatialReference("");
        dstSpatialRef.ImportFromEPSG(4326); // WGS84
        var coordTransform = new CoordinateTransformation(srcSpatialRef, dstSpatialRef);

        // Вычисление географических координат углов
        (double corner1Lon, double corner1Lat) = PixelToLatLon(0, 0, geoTransform, coordTransform);
        (double corner2Lon, double corner2Lat) = PixelToLatLon(width - 1, 0, geoTransform, coordTransform);
        (double corner3Lon, double corner3Lat) = PixelToLatLon(width - 1, height - 1, geoTransform, coordTransform);
        (double corner4Lon, double corner4Lat) = PixelToLatLon(0, height - 1, geoTransform, coordTransform);

        // Вычисление минимальных и максимальных долготы и широты
        double minLon = Math.Min(Math.Min(corner1Lon, corner2Lon), Math.Min(corner3Lon, corner4Lon));
        double maxLon = Math.Max(Math.Max(corner1Lon, corner2Lon), Math.Max(corner3Lon, corner4Lon));

        double minLat = Math.Min(Math.Min(corner1Lat, corner2Lat), Math.Min(corner3Lat, corner4Lat));
        double maxLat = Math.Max(Math.Max(corner1Lat, corner2Lat), Math.Max(corner3Lat, corner4Lat));

        // Ограничение значений долготы и широты
        minLon = Math.Max(-180.0, Math.Min(179.999999, minLon));
        maxLon = Math.Max(-180.0, Math.Min(179.999999, maxLon));
        minLat = Math.Max(-85.05112878, Math.Min(85.05112878, minLat));
        maxLat = Math.Max(-85.05112878, Math.Min(85.05112878, maxLat));

        // Выбор уровня зума для тайлов карты
        int zoom = CalculateZoomLevel(minLon, maxLon, imageWidth);

        // Вычисление индексов тайлов
        int xTileMin = LonToTileX(minLon, zoom);
        int xTileMax = LonToTileX(maxLon, zoom);
        int yTileMin = LatToTileY(maxLat, zoom);
        int yTileMax = LatToTileY(minLat, zoom);

        int maxTileIndex = (1 << zoom) - 1;
        xTileMin = Math.Clamp(xTileMin, 0, maxTileIndex);
        xTileMax = Math.Clamp(xTileMax, 0, maxTileIndex);
        yTileMin = Math.Clamp(yTileMin, 0, maxTileIndex);
        yTileMax = Math.Clamp(yTileMax, 0, maxTileIndex);

        if(xTileMin > xTileMax) {
            (xTileMax, xTileMin) = (xTileMin, xTileMax);
        }

        if(yTileMin > yTileMax) {
            (yTileMax, yTileMin) = (yTileMin, yTileMax);
        }

        // Создание фонового изображения карты
        SKBitmap backgroundBitmap = CreateBackgroundMap(zoom, xTileMin, xTileMax, yTileMin, yTileMax);

        // Вычисление географических границ фонового изображения
        double backgroundMinLon = TileXToLon(xTileMin, zoom);
        double backgroundMaxLon = TileXToLon(xTileMax + 1, zoom);
        double backgroundMaxLat = TileYToLat(yTileMin, zoom);
        double backgroundMinLat = TileYToLat(yTileMax + 1, zoom);

        // Вычисление координат для отображения фоновой карты
        double x0 = (minLon - backgroundMinLon) / (backgroundMaxLon - backgroundMinLon) * backgroundBitmap.Width;
        double x1 = (maxLon - backgroundMinLon) / (backgroundMaxLon - backgroundMinLon) * backgroundBitmap.Width;
        double y0 = (backgroundMaxLat - maxLat) / (backgroundMaxLat - backgroundMinLat) * backgroundBitmap.Height;
        double y1 = (backgroundMaxLat - minLat) / (backgroundMaxLat - backgroundMinLat) * backgroundBitmap.Height;

        var sourceRect = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        var destRect = new SKRect(0, 0, imageWidth, imageHeight);

        // Нормализация данных для отображения
        float minValue = data.Min();
        float maxValue = data.Max();
        float valueRange = maxValue - minValue;
        float normalizationFactor = 1f / valueRange;

        float interval = valueRange / contourLevels;

        // Предварительный расчет цветовой палитры
        var colorPalette = new SKColor[colorPaletteSize];
        for(int i = 0; i < colorPaletteSize; i++) {
            float normalizedValue = i / (colorPaletteSize - 1f);
            colorPalette[i] = SKColor.FromHsl(240 - 240 * normalizedValue, 100, 50);
        }

        using var bitmap = new SKBitmap(imageWidth, imageHeight);
        using var canvas = new SKCanvas(bitmap);

        // Отрисовка фоновой карты
        canvas.DrawBitmap(backgroundBitmap, sourceRect, destRect);

        // Билинейная интерполяция для каждого пикселя (параллельно)
        float imageToDataScaleX = (width - 1) / (float)(imageWidth - 1);
        float imageToDataScaleY = (height - 1) / (float)(imageHeight - 1);

        // Создаем массив пикселей для наложения данных
        var dataBitmap = new SKBitmap(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        IntPtr pixelsAddr = dataBitmap.GetPixels();
        int bytesPerPixel = dataBitmap.BytesPerPixel;
        int rowBytes = dataBitmap.RowBytes;
        byte[] pixelBuffer = new byte[dataBitmap.ByteCount];

        float alphaFactor = desiredAlpha / 255f;

        Parallel.For(0, imageHeight, y => {
            float originalY = y * imageToDataScaleY;
            int y0 = (int)originalY;
            int y1 = Math.Min(y0 + 1, height - 1);
            float fy = originalY - y0;

            for(int x = 0; x < imageWidth; x++) {
                float originalX = x * imageToDataScaleX;
                int x0 = (int)originalX;
                int x1 = Math.Min(x0 + 1, width - 1);

                float fx = originalX - x0;

                float v00 = data[y0 * width + x0];
                float v10 = data[y0 * width + x1];
                float v01 = data[y1 * width + x0];
                float v11 = data[y1 * width + x1];

                // Билинейная интерполяция
                float interpolatedValue =
                    v00 * (1 - fx) * (1 - fy) +
                    v10 * fx * (1 - fy) +
                    v01 * (1 - fx) * fy +
                    v11 * fx * fy;

                // Нормализация и получение цвета
                float normalizedValue = (interpolatedValue - minValue) * normalizationFactor;
                int colorIndex = (int)(normalizedValue * (colorPaletteSize - 1));
                colorIndex = Math.Clamp(colorIndex, 0, colorPaletteSize - 1);
                SKColor color = colorPalette[colorIndex];

                // Вычисление позиции пикселя в буфере
                int pixelIndex = y * rowBytes + x * bytesPerPixel;

                // Запись цвета в буфер пикселей
                pixelBuffer[pixelIndex + 0] = (byte)(color.Blue * alphaFactor);
                pixelBuffer[pixelIndex + 1] = (byte)(color.Green * alphaFactor);
                pixelBuffer[pixelIndex + 2] = (byte)(color.Red * alphaFactor);
                pixelBuffer[pixelIndex + 3] = desiredAlpha;
            }
        });

        // Копирование буфера пикселей в bitmap
        System.Runtime.InteropServices.Marshal.Copy(pixelBuffer, 0, pixelsAddr, pixelBuffer.Length);

        canvas.DrawBitmap(dataBitmap, new SKPoint(0, 0));

        // Параметры для контурных линий с динамической толщиной
        float scalingFactor = imageWidth / baseResolution;
        float strokeWidth = baseStrokeWidth * scalingFactor;

        using var contourPaint = new SKPaint {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            Color = SKColors.Black.WithAlpha(contourAlpha)
        };

        // Отрисовка контурных линий
        for(int level = 1; level < contourLevels; level++) {
            float contourValue = minValue + level * interval;
            DrawContours(canvas, contourPaint, data, width, height, imageWidth, imageHeight, contourValue);
        }

        // Сохранение изображения для текущего слоя
        string outputPath = Path.Combine(outputDirectory, $"{band.GetMetadataItem("GRIB_ELEMENT", "")}-{band.GetDescription()}.png");
        using var image = SKImage.FromBitmap(bitmap);
        using SKData dataImage = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.OpenWrite(outputPath);
        dataImage.SaveTo(stream);

        Console.WriteLine($"Карта для слоя {layerIndex} сохранена в {outputPath}");
    }

    // Функция для вычисления уровня зума
    static int CalculateZoomLevel(double minLon, double maxLon, int imageWidth) {
        for(int z = 18; z >= 0; z--) {
            double worldPixelWidth = 256 * Math.Pow(2, z);
            double lonPerPixel = 360.0 / worldPixelWidth;
            double pixelWidth = (maxLon - minLon) / lonPerPixel;
            if(pixelWidth <= imageWidth) {
                return z;
            }
        }

        return 0;
    }

    // Функция для создания фоновой карты
    static SKBitmap CreateBackgroundMap(int zoom, int xTileMin, int xTileMax, int yTileMin, int yTileMax) {
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
    static SKBitmap? GetTile(int zoom, int xTile, int yTile) {
        string tilePath = Path.Combine(tileCacheDirectory, zoom.ToString(), xTile.ToString(), yTile.ToString() + ".png");

        if(File.Exists(tilePath)) {
            // Загрузка тайла из кэша
            using FileStream stream = File.OpenRead(tilePath);
            return SKBitmap.Decode(stream);
        } else {
            // Скачивание тайла
            string url = string.Format(tileUrlTemplate, zoom, xTile, yTile);

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

    // Функции для преобразования координат тайлов и географических координат
    static int LonToTileX(double lon, int zoom) {
        lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0; // Нормализация долготы [-180, 180)
        double n = (lon + 180.0) / 360.0;
        int tileX = (int)Math.Floor(n * (1 << zoom));
        tileX = Math.Clamp(tileX, 0, (1 << zoom) - 1);
        return tileX;
    }

    static int LatToTileY(double lat, int zoom) {
        lat = Math.Max(-85.05112878, Math.Min(85.05112878, lat)); // Ограничение широты
        double latRad = lat * Math.PI / 180.0;
        double n = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0;
        int tileY = (int)Math.Floor(n * (1 << zoom));
        tileY = Math.Clamp(tileY, 0, (1 << zoom) - 1);
        return tileY;
    }

    static double TileXToLon(int xTile, int zoom) => xTile / Math.Pow(2.0, zoom) * 360.0 - 180;

    static double TileYToLat(int yTile, int zoom) {
        double n = Math.PI - 2.0 * Math.PI * yTile / Math.Pow(2.0, zoom);
        return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
    }

    static (double lon, double lat) PixelToLatLon(double xPixel, double yPixel, double[] geoTransform, CoordinateTransformation coordTransform) {
        double xGeo = geoTransform[0] + xPixel * geoTransform[1] + yPixel * geoTransform[2];
        double yGeo = geoTransform[3] + xPixel * geoTransform[4] + yPixel * geoTransform[5];

        double[] transformed = new double[3];
        coordTransform.TransformPoint(transformed, xGeo, yGeo, 0);

        return (transformed[0], transformed[1]);
    }

    static void DrawContours(SKCanvas canvas, SKPaint contourPaint, float[] data, int width, int height, int imageWidth, int imageHeight, float contourValue) {
        float scaleX = imageWidth / (float)(width - 1);
        float scaleY = imageHeight / (float)(height - 1);

        // Для каждой ячейки в сетке
        Parallel.For(0, height - 1, y => {
            var linesToDraw = new List<(SKPoint, SKPoint)>(); // Список для хранения линий для каждого потока

            for(int x = 0; x < width - 1; x++) {
                // Индексы углов
                int idx0 = y * width + x;           // Вершина 0
                int idx1 = y * width + x + 1;       // Вершина 1
                int idx2 = (y + 1) * width + x + 1; // Вершина 2
                int idx3 = (y + 1) * width + x;     // Вершина 3

                // Значения в углах
                float v0 = data[idx0];
                float v1 = data[idx1];
                float v2 = data[idx2];
                float v3 = data[idx3];

                // Определение индекса в таблице линий
                int squareIndex = 0;
                if(v0 < contourValue) squareIndex |= 1;
                if(v1 < contourValue) squareIndex |= 2;
                if(v2 < contourValue) squareIndex |= 4;
                if(v3 < contourValue) squareIndex |= 8;

                // Пропуск, если ячейка полностью внутри или вне контура
                if(squareIndex is 0 or 15)
                    continue;

                // Интерполяция точек пересечения и сохранение линий
                for(int i = 0; i < 4; i += 2) {
                    if(lineTable[squareIndex, i] == -1 || lineTable[squareIndex, i + 1] == -1)
                        break;

                    SKPoint p1 = InterpolateEdge(x, y, v0, v1, v2, v3, lineTable[squareIndex, i], contourValue, scaleX, scaleY);
                    SKPoint p2 = InterpolateEdge(x, y, v0, v1, v2, v3, lineTable[squareIndex, i + 1], contourValue, scaleX, scaleY);

                    linesToDraw.Add((p1, p2)); // Сбор линий вместо немедленной отрисовки
                }
            }

            // Блокировка и отрисовка собранных линий для данного потока
            lock(canvas) {
                foreach((SKPoint, SKPoint) line in linesToDraw) {
                    canvas.DrawLine(line.Item1, line.Item2, contourPaint);
                }
            }
        });
    }

    static SKPoint InterpolateEdge(int x, int y, float v0, float v1, float v2, float v3, int edge, float contourValue, float scaleX, float scaleY) {
        float x1 = x, y1 = y, x2 = x, y2 = y;
        float value1 = 0f, value2 = 0f;

        switch(edge) {
            case 0: // Ребро между v0 и v1 (нижнее ребро)
                x1 = x;
                y1 = y;
                x2 = x + 1;
                y2 = y;
                value1 = v0;
                value2 = v1;
                break;
            case 1: // Ребро между v1 и v2 (правое ребро)
                x1 = x + 1;
                y1 = y;
                x2 = x + 1;
                y2 = y + 1;
                value1 = v1;
                value2 = v2;
                break;
            case 2: // Ребро между v2 и v3 (верхнее ребро)
                x1 = x + 1;
                y1 = y + 1;
                x2 = x;
                y2 = y + 1;
                value1 = v2;
                value2 = v3;
                break;
            case 3: // Ребро между v3 и v0 (левое ребро)
                x1 = x;
                y1 = y + 1;
                x2 = x;
                y2 = y;
                value1 = v3;
                value2 = v0;
                break;
        }

        float t = (contourValue - value1) / (value2 - value1 + epsilon); // Избегаем деления на ноль
        t = Math.Clamp(t, 0f, 1f);

        float ix = (x1 + t * (x2 - x1)) * scaleX;
        float iy = (y1 + t * (y2 - y1)) * scaleY;

        return new SKPoint(ix, iy);
    }
}
