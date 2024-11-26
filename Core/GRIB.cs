using OSGeo.GDAL;
using OSGeo.OSR;

using SkiaSharp;

namespace Core {

    public class GRIB {
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

        // Настройки изображения
        private readonly int imageWidth;
        private readonly int imageHeight;

        // Количество уровней контуров для большей детализации
        private readonly int contourLevels;

        // Уровень прозрачности (0 - полностью прозрачный, 255 - полностью непрозрачный)
        private readonly byte desiredAlpha;

        // Параметры для динамической толщины линии
        private static readonly float baseResolution = 1920f; // Базовая ширина разрешения
        private static readonly float baseStrokeWidth = .5f; // Базовая толщина линии при базовом разрешении

        // Малое число для избежания деления на ноль
        private static readonly float epsilon = 1e-7f;

        // Выходной каталог для сохранения карт
        private readonly string outputDirectory;

        private static readonly BackgroundMapManager backgroundMapManager = new();

        public GRIB(int imageWidth = 30720, int imageHeight = 8640, int contourLevels = 15, byte desiredAlpha = 84, string outputDirectory = "output_maps") {
            this.imageWidth = imageWidth;
            this.imageHeight = imageHeight;
            this.contourLevels = contourLevels;
            this.desiredAlpha = desiredAlpha;
            this.outputDirectory = outputDirectory;

            Gdal.AllRegister();
        }

        public static bool IsGRIB(string filePath) {
            using Dataset dataset = Gdal.Open(filePath, Access.GA_ReadOnly);

            return dataset != null;
        }

        public void DrawAllMaps(string filePath) {
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
                DrawLayerWithSmoothContours(dataset, band, i);
            }

            Console.WriteLine($"Все карты успешно сохранены в {outputDirectory}");
        }

        private static CoordinateTransformation GetCoordinateTransformation(Dataset dataset) {
            var srcSpatialRef = new SpatialReference(dataset.GetProjectionRef());
            var dstSpatialRef = new SpatialReference("");
            dstSpatialRef.ImportFromEPSG(4326); // WGS84
            return new CoordinateTransformation(srcSpatialRef, dstSpatialRef);
        }

        private static (double minLon, double maxLon, double minLat, double maxLat) GetImageBounds(double[] geoTransform, CoordinateTransformation coordTransform, int width, int height) {
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

            return (minLon, maxLon, minLat, maxLat);
        }

        private static (int xTileMin, int xTileMax, int yTileMin, int yTileMax) GetTileIndices(double minLon, double maxLon, double minLat, double maxLat, int zoom) {

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

            if(xTileMin > xTileMax) (xTileMax, xTileMin) = (xTileMin, xTileMax);

            if(yTileMin > yTileMax) (yTileMax, yTileMin) = (yTileMin, yTileMax);

            return (xTileMin, xTileMax, yTileMin, yTileMax);
        }

        private void DrawBackgroundMap(SKCanvas canvas, SKBitmap backgroundBitmap, int xTileMin, int xTileMax, int yTileMin, int yTileMax, int zoom, double minLon, double maxLon, double minLat, double maxLat) {

            // Вычисление географических границ фонового изображения
            double backgroundMinLon = TileXToLon(xTileMin, zoom);
            double backgroundMaxLon = TileXToLon(xTileMax + 1, zoom);
            double backgroundMaxLat = TileYToLat(yTileMin, zoom);
            double backgroundMinLat = TileYToLat(yTileMax + 1, zoom);

            double x0 = (minLon - backgroundMinLon) / (backgroundMaxLon - backgroundMinLon) * backgroundBitmap.Width;
            double x1 = (maxLon - backgroundMinLon) / (backgroundMaxLon - backgroundMinLon) * backgroundBitmap.Width;
            double y0 = (backgroundMaxLat - maxLat) / (backgroundMaxLat - backgroundMinLat) * backgroundBitmap.Height;
            double y1 = (backgroundMaxLat - minLat) / (backgroundMaxLat - backgroundMinLat) * backgroundBitmap.Height;

            var sourceRect = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
            var destRect = new SKRect(0, 0, imageWidth, imageHeight);

            canvas.DrawBitmap(backgroundBitmap, sourceRect, destRect);
        }

        private static (SKColor[] colorPalette, float normalizationFactor) PrepareColorPalette(float[] data, float valueRange) {
            float normalizationFactor = 1f / valueRange;

            // Предварительный расчет цветовой палитры
            var colorPalette = new SKColor[256];
            for(int i = 0; i < 256; i++) {
                float normalizedValue = i / 255f;
                colorPalette[i] = SKColor.FromHsl(240 - 240 * normalizedValue, 100, 50);
            }

            return (colorPalette, normalizationFactor);
        }

        private void DrawDataMap(SKCanvas canvas, float[] data, int width, int height, SKColor[] colorPalette, float normalizationFactor) {
            float minValue = data.Min();

            // Билинейная интерполяция для каждого пикселя (параллельно)
            float imageToDataScaleX = (width - 1) / (float)(imageWidth - 1);
            float imageToDataScaleY = (height - 1) / (float)(imageHeight - 1);

            // Создаем массив пикселей для наложения данных
            var dataBitmap = new SKBitmap(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            nint pixelsAddr = dataBitmap.GetPixels();
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
                    int colorIndex = (int)(normalizedValue * 255);
                    colorIndex = Math.Clamp(colorIndex, 0, 255);
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
        }

        private void DrawContoursMap(SKCanvas canvas, float[] data, int width, int height, float minValue, float valueRange) {
            // Параметры для контурных линий с динамической толщиной
            float scalingFactor = imageWidth / baseResolution;
            float strokeWidth = baseStrokeWidth * scalingFactor;
            float interval = valueRange / contourLevels;

            using var contourPaint = new SKPaint {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                StrokeWidth = strokeWidth,
                BlendMode = SKBlendMode.Multiply,
                Color = SKColors.Black
            };

            // Отрисовка контурных линий
            for(int level = 1; level < contourLevels; level++) {
                float contourValue = minValue + level * interval;
                DrawContours(canvas, contourPaint, data, width, height, contourValue, strokeWidth);
            }
        }

        private void SaveLayerImage(SKBitmap bitmap, Band band, int layerIndex) {
            string outputPath = Path.Combine(outputDirectory, $"{band.GetMetadataItem("GRIB_ELEMENT", "")}-{band.GetDescription()}.png");
            using var image = SKImage.FromBitmap(bitmap);
            using SKData dataImage = image.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream stream = File.OpenWrite(outputPath);
            dataImage.SaveTo(stream);

            Console.WriteLine($"Карта для слоя {layerIndex} сохранена в {outputPath}");
        }

        private void DrawLayerWithSmoothContours(Dataset dataset, Band band, int layerIndex) {

            // Чтение метаинформации и подготовка данных
            int width = band.XSize;
            int height = band.YSize;
            float[] data = new float[width * height];
            band.ReadRaster(0, 0, width, height, data, width, height, 0, 0);

            // Получение геотрансформации и преобразования координат
            double[] geoTransform = new double[6];
            dataset.GetGeoTransform(geoTransform);

            // Преобразование координат
            CoordinateTransformation coordTransform = GetCoordinateTransformation(dataset);

            // Вычисление географических границ изображения
            (double minLon, double maxLon, double minLat, double maxLat) = GetImageBounds(geoTransform, coordTransform, width, height);

            // Выбор уровня зума для тайлов карты
            int zoom = backgroundMapManager.GetZoomLevel(minLon, maxLon, imageWidth);

            // Тайловые индексы
            (int xTileMin, int xTileMax, int yTileMin, int yTileMax) = GetTileIndices(minLon, maxLon, minLat, maxLat, zoom);

            // Создание фонового изображения карты
            SKBitmap backgroundBitmap = backgroundMapManager.GetBackgroundMap(zoom, xTileMin, xTileMax, yTileMin, yTileMax);

            SKBitmap resultBitmap = new(imageWidth, imageHeight);
            using var canvas = new SKCanvas(resultBitmap);
            DrawBackgroundMap(canvas, backgroundBitmap, xTileMin, xTileMax, yTileMin, yTileMax, zoom, minLon, maxLon, minLat, maxLat);

            // Нормализация данных для отображения
            float minValue = data.Min();
            float maxValue = data.Max();
            float valueRange = maxValue - minValue;

            // Нормализация данных для цветовой палитры
            (SKColor[] colorPalette, float normalizationFactor) = PrepareColorPalette(data, valueRange);

            // Создание карты данных с билинейной интерполяцией
            DrawDataMap(canvas, data, width, height, colorPalette, normalizationFactor);

            // Отрисовка контуров
            DrawContoursMap(canvas, data, width, height, minValue, valueRange);

            // Сохранение изображения
            SaveLayerImage(resultBitmap, band, layerIndex);
        }

        // Функции для преобразования координат тайлов и географических координат
        private static int LonToTileX(double lon, int zoom) {
            lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0; // Нормализация долготы [-180, 180)
            double n = (lon + 180.0) / 360.0;
            int tileX = (int)Math.Floor(n * (1 << zoom));
            tileX = Math.Clamp(tileX, 0, (1 << zoom) - 1);
            return tileX;
        }

        private static int LatToTileY(double lat, int zoom) {
            lat = Math.Max(-85.05112878, Math.Min(85.05112878, lat)); // Ограничение широты
            double latRad = lat * Math.PI / 180.0;
            double n = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0;
            int tileY = (int)Math.Floor(n * (1 << zoom));
            tileY = Math.Clamp(tileY, 0, (1 << zoom) - 1);
            return tileY;
        }

        private static double TileXToLon(int xTile, int zoom) => xTile / Math.Pow(2.0, zoom) * 360.0 - 180;

        private static double TileYToLat(int yTile, int zoom) {
            double n = Math.PI - 2.0 * Math.PI * yTile / Math.Pow(2.0, zoom);
            return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        }

        private static (double lon, double lat) PixelToLatLon(double xPixel, double yPixel, double[] geoTransform, CoordinateTransformation coordTransform) {
            double xGeo = geoTransform[0] + xPixel * geoTransform[1] + yPixel * geoTransform[2];
            double yGeo = geoTransform[3] + xPixel * geoTransform[4] + yPixel * geoTransform[5];

            double[] transformed = new double[3];
            coordTransform.TransformPoint(transformed, xGeo, yGeo, 0);

            return (transformed[0], transformed[1]);
        }

        private void DrawContours(SKCanvas canvas, SKPaint contourPaint, float[] data, int width, int height, float contourValue, float strokeWidth) {
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
                    foreach((SKPoint p0, SKPoint p1) line in linesToDraw) {
                        canvas.DrawLine(line.p0, line.p1, contourPaint);
                        canvas.DrawCircle(line.p0, strokeWidth / 2, contourPaint);
                        canvas.DrawCircle(line.p1, strokeWidth / 2, contourPaint);
                    }
                }
            });
        }

        private static SKPoint InterpolateEdge(int x, int y, float v0, float v1, float v2, float v3, int edge, float contourValue, float scaleX, float scaleY) {
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
}