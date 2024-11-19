using OSGeo.GDAL;

using SkiaSharp;

class Program {
    // Настройки изображения
    private static readonly int imageWidth = 4096; // Ширина изображения
    private static readonly int imageHeight = 3072; // Высота изображения

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

    // Параметры для динамической толщины линии
    private static readonly float baseResolution = 1920f; // Базовая ширина разрешения
    private static readonly float baseStrokeWidth = 1.5f; // Базовая толщина линии при базовом разрешении

    // Малое число для избежания деления на ноль
    private static readonly float epsilon = 1e-7f;

    // Путь к файлу GRIB
    private static readonly string filePath = "GRIBNOA00.000.1";

    // Выходной каталог для сохранения карт
    private static readonly string outputDirectory = "output_maps";

    static void Main(string[] args) {
        // Инициализация GDAL
        Gdal.AllRegister();

        // Путь к вашему GRIB файлу
        string filePath = "GRIBNOA00.000.1";
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
            DrawLayerWithSmoothContours(band, outputDirectory, i);
        }

        Console.WriteLine($"Все карты успешно сохранены в {outputDirectory}");
    }

    static void DrawLayerWithSmoothContours(Band band, string outputDirectory, int layerIndex) {
        int width = band.XSize;
        int height = band.YSize;

        // Чтение данных из слоя
        float[] data = new float[width * height];
        band.ReadRaster(0, 0, width, height, data, width, height, 0, 0);

        // Предварительный расчет констант
        float imageToDataScaleX = (width - 1) / (float)(imageWidth - 1);
        float imageToDataScaleY = (height - 1) / (float)(imageHeight - 1);

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

        // Прямой доступ к буферу пикселей
        IntPtr pixelsAddr = bitmap.GetPixels();
        int bytesPerPixel = bitmap.BytesPerPixel;
        int rowBytes = bitmap.RowBytes;
        byte[] pixelBuffer = new byte[bitmap.ByteCount];

        // Билинейная интерполяция для каждого пикселя (параллельно)
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
                pixelBuffer[pixelIndex + 0] = color.Blue;
                pixelBuffer[pixelIndex + 1] = color.Green;
                pixelBuffer[pixelIndex + 2] = color.Red;
                pixelBuffer[pixelIndex + 3] = color.Alpha;
            }
        });

        // Копирование буфера пикселей в bitmap
        System.Runtime.InteropServices.Marshal.Copy(pixelBuffer, 0, pixelsAddr, pixelBuffer.Length);

        using var canvas = new SKCanvas(bitmap);

        // Параметры для контурных линий с динамической толщиной
        float scalingFactor = imageWidth / baseResolution;
        float strokeWidth = baseStrokeWidth * scalingFactor;

        using var contourPaint = new SKPaint {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            Color = SKColors.Black.WithAlpha(128)
        };

        // Отрисовка контурных линий
        for(int level = 1; level < contourLevels; level++) {
            float contourValue = minValue + level * interval;
            DrawContours(canvas, contourPaint, data, width, height, imageWidth, imageHeight, contourValue);
        }

        // Сохранение изображения для текущего слоя
        string outputPath = Path.Combine(outputDirectory, $"map_layer_{layerIndex}.png");
        using var image = SKImage.FromBitmap(bitmap);
        using SKData dataImage = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.OpenWrite(outputPath);
        dataImage.SaveTo(stream);

        Console.WriteLine($"Карта для слоя {layerIndex} сохранена в {outputPath}");
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
