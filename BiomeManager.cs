using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using System.Collections.Generic;
using System.Linq;
using TerrainGeneration;

public class BiomeManager : MonoBehaviour
{
    [Header("Настройки биомов")]
    [SerializeField] private List<BiomeData> biomes = new List<BiomeData>();

    [SerializeField] private BiomeData defaultBiome;

    [Tooltip("Использовать карту биомов вместо процедурной генерации")]
    [SerializeField] private bool useCustomBiomeMap = false;

    [Tooltip("Карта биомов (для нажатия Enable)")]
    [SerializeField] private Texture2D biomeMap;

    [Tooltip("Масштаб карты биомов")]
    [SerializeField] private float biomeMapScale = 1f;

    [Header("Настройки шума для биомов")]
    [SerializeField] private float biomeSeed = 0f;

    [Tooltip("Масштаб шума для температуры")]
    [SerializeField] private float temperatureNoiseScale = 0.01f;

    [Tooltip("Масштаб шума для влажности")]
    [SerializeField] private float humidityNoiseScale = 0.01f;

    [Tooltip("Сила смешивания биомов на границах")]
    [Range(0f, 1f)]
    [SerializeField] private float biomeBlendStrength = 0.3f;

    [Tooltip("Смещение шума для температуры")]
    [SerializeField] private Vector2 temperatureOffset = Vector2.zero;

    [Tooltip("Смещение шума для влажности")]
    [SerializeField] private Vector2 humidityOffset = Vector2.zero;

    [Header("Кеширование и производительность")]
    [Tooltip("Размер кеша температуры и влажности")]
    [SerializeField] private int cacheSizeInCells = 1024;

    [SerializeField] private bool enableBiomeCaching = true;

    [Tooltip("Качество биомов - влияет на скорость и качество генерации")]
    [Range(1, 4)]
    [SerializeField] private int biomeQuality = 2;

    [Header("Оптимизация растительности")]
    [Tooltip("Включить инстансинг для растительности")]
    [SerializeField] private bool useVegetationInstancing = true;

    [Tooltip("Максимальное расстояние отрисовки растительности")]
    [SerializeField] private float vegetationDrawDistance = 200f;

    [Tooltip("Использовать LOD для растительности")]
    [SerializeField] private bool useVegetationLOD = true;

    [Tooltip("Максимальное количество экземпляров растительности на чанк")]
    [SerializeField] private int maxVegetationPerChunk = 500;

    [Header("Отладка")]
    [SerializeField] private bool showBiomeGizmos = true;

    [SerializeField] private bool showTemperatureMap = false;

    [SerializeField] private bool showHumidityMap = false;

    [SerializeField] private bool logBiomeGeneration = false;

    // Ссылка на генератор ландшафта
    private TerrainGenerator terrainGenerator;

    // Кеш для биомов
    private Dictionary<Vector2Int, BiomeData> biomeCache = new Dictionary<Vector2Int, BiomeData>();

    // Кеш для температуры и влажности
    private Dictionary<Vector2Int, float2> climateCache = new Dictionary<Vector2Int, float2>();

    // Пулы для инстансинга растительности
    private Dictionary<string, List<GameObject>> vegetationPools = new Dictionary<string, List<GameObject>>();

    // Список всей размещенной растительности
    private List<GameObject> placedVegetation = new List<GameObject>();

    // Флаг для отслеживания необходимости повторной генерации растительности
    private bool needsVegetationRebuild = false;

    // Текущая камера для оптимизации растительности
    private Camera mainCamera;

    // Массив для хранения информации о биомах для шейдера
    private Vector4[] biomeInfoArray;

    // Текущая сетка биомов для визуализации
    private Vector4[,] biomeGridForGizmos;

    // Признак инициализации
    private bool isInitialized = false;

    // Префабы растительности
    private Dictionary<string, GameObject> vegetationPrefabs = new Dictionary<string, GameObject>();

    // Пути к текстурам
    private Dictionary<string, string> texturePathCache = new Dictionary<string, string>();

    // Unity события
    private void OnEnable()
    {
        // Получаем ссылку на генератор ландшафта
        terrainGenerator = GetComponent<TerrainGenerator>();

        if (terrainGenerator == null)
        {
            Debug.LogError("TerrainGenerator не найден на объекте!");
            return;
        }

        Initialize();
    }

    private void Start()
    {
        mainCamera = Camera.main;

        if (mainCamera == null)
        {
            Debug.LogWarning("Главная камера не найдена! Некоторые оптимизации будут отключены.");
        }

        RegenerateVegetation();
    }

    private void Update()
    {
        if (needsVegetationRebuild)
        {
            RegenerateVegetation();
            needsVegetationRebuild = false;
        }

        if (mainCamera != null && useVegetationLOD)
        {
            UpdateVegetationVisibility();
        }
    }

    /// <summary>
    /// Инициализация менеджера биомов
    /// </summary>
    public void Initialize()
    {
        if (isInitialized)
            return;

        // Если биомы не заданы, добавляем хотя бы один по умолчанию
        if (biomes.Count == 0)
        {
            defaultBiome = BiomeData.GetDefaultBiome(BiomeType.Plains);
            biomes.Add(defaultBiome);
        }

        // Если не указан биом по умолчанию, берем первый из списка
        if (defaultBiome == null && biomes.Count > 0)
        {
            defaultBiome = biomes[0];
        }

        // Инициализируем кеши
        biomeCache.Clear();
        climateCache.Clear();

        // Инициализируем массив информации о биомах для шейдера
        biomeInfoArray = new Vector4[biomes.Count];
        for (int i = 0; i < biomes.Count; i++)
        {
            biomeInfoArray[i] = new Vector4(
                biomes[i].temperature,
                biomes[i].humidity,
                biomes[i].baseHeight,
                biomes[i].snowHeight
            );
        }

        // Предварительно кешируем часть данных для ускорения
        if (enableBiomeCaching)
        {
            PreCacheClimateData();
        }

        // Регистрируем события от террейн генератора
        RegisterTerrainEvents();

        isInitialized = true;

        if (logBiomeGeneration)
        {
            Debug.Log($"BiomeManager инициализирован с {biomes.Count} биомами");
        }
    }

    /// <summary>
    /// Регистрация обработчиков событий от террейн генератора
    /// </summary>
    private void RegisterTerrainEvents()
    {
        // Если в TerrainGenerator есть события, подписываемся на них
        // Пример: если в TerrainGenerator будет событие OnTerrainGenerated
        // terrainGenerator.OnTerrainGenerated += OnTerrainGenerated;
    }

    /// <summary>
    /// Предварительное кеширование данных о температуре и влажности
    /// для часто используемых областей
    /// </summary>
    private void PreCacheClimateData()
    {
        int halfSize = cacheSizeInCells / 2;
        int chunkSize = (int)terrainGenerator.ChunkSize;

        var climateJob = new ClimateCalculationJob
        {
            XStart = -halfSize,
            ZStart = -halfSize,
            Width = cacheSizeInCells,
            Height = cacheSizeInCells,
            TempNoiseScale = temperatureNoiseScale,
            HumidityNoiseScale = humidityNoiseScale,
            TemperatureOffset = new float2(temperatureOffset.x, temperatureOffset.y),
            HumidityOffset = new float2(humidityOffset.x, humidityOffset.y),
            ChunkSize = chunkSize,
            Seed = biomeSeed,
            Results = new NativeArray<float2>(cacheSizeInCells * cacheSizeInCells, Allocator.TempJob)
        };

        JobHandle handle = climateJob.Schedule(cacheSizeInCells * cacheSizeInCells, 64);
        handle.Complete();

        // Копируем результаты в кеш
        for (int z = 0; z < cacheSizeInCells; z++)
        {
            for (int x = 0; x < cacheSizeInCells; x++)
            {
                int index = z * cacheSizeInCells + x;
                int worldX = x - halfSize;
                int worldZ = z - halfSize;

                climateCache[new Vector2Int(worldX, worldZ)] = climateJob.Results[index];
            }
        }

        climateJob.Results.Dispose();
    }

    /// <summary>
    /// Получение биома для указанной позиции мира
    /// </summary>
    public BiomeData GetBiomeAt(Vector3 worldPosition)
    {
        return GetBiomeAt(worldPosition.x, worldPosition.z);
    }

    /// <summary>
    /// Получение биома для указанных координат
    /// </summary>
    public BiomeData GetBiomeAt(float x, float z)
    {
        // Если используем карту биомов
        if (useCustomBiomeMap && biomeMap != null)
        {
            return GetBiomeFromMap(x, z);
        }

        // Вычисляем координаты с учетом масштаба чанка
        int chunkSize = (int)terrainGenerator.ChunkSize;
        int chunkX = Mathf.FloorToInt(x / chunkSize);
        int chunkZ = Mathf.FloorToInt(z / chunkSize);

        Vector2Int key = new Vector2Int(chunkX, chunkZ);

        // Проверяем кеш
        if (biomeCache.TryGetValue(key, out BiomeData cachedBiome))
        {
            return cachedBiome;
        }

        // Получаем климатические данные
        float2 climate = GetClimateData(x, z);
        float temperature = climate.x;
        float humidity = climate.y;

        // Находим наиболее подходящий биом на основе температуры и влажности
        BiomeData bestBiome = defaultBiome;
        float bestMatch = float.MaxValue;

        foreach (var biome in biomes)
        {
            // Вычисляем "расстояние" в климатическом пространстве
            float tempDiff = Mathf.Abs(biome.temperature - temperature);
            float humidityDiff = Mathf.Abs(biome.humidity - humidity);
            float match = tempDiff * tempDiff + humidityDiff * humidityDiff;

            if (match < bestMatch)
            {
                bestMatch = match;
                bestBiome = biome;
            }
        }

        // Кешируем результат
        biomeCache[key] = bestBiome;

        return bestBiome;
    }

    /// <summary>
    /// Получение биома из карты биомов
    /// </summary>
    private BiomeData GetBiomeFromMap(float x, float z)
    {
        if (biomeMap == null)
            return defaultBiome;

        // Преобразуем мировые координаты в координаты текстуры
        int pixelX = Mathf.FloorToInt((x / biomeMapScale) % biomeMap.width);
        int pixelZ = Mathf.FloorToInt((z / biomeMapScale) % biomeMap.height);

        // Обработка отрицательных координат
        if (pixelX < 0) pixelX += biomeMap.width;
        if (pixelZ < 0) pixelZ += biomeMap.height;

        // Получаем цвет с карты биомов
        Color pixelColor = biomeMap.GetPixel(pixelX, pixelZ);

        // Находим ближайший биом по цвету
        BiomeData closestBiome = defaultBiome;
        float closestDistance = float.MaxValue;

        foreach (var biome in biomes)
        {
            float distance = ColorDistance(pixelColor, biome.editorColor);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestBiome = biome;
            }
        }

        return closestBiome;
    }

    /// <summary>
    /// Вычисление расстояния между цветами
    /// </summary>
    private float ColorDistance(Color c1, Color c2)
    {
        float rDiff = c1.r - c2.r;
        float gDiff = c1.g - c2.g;
        float bDiff = c1.b - c2.b;
        return rDiff * rDiff + gDiff * gDiff + bDiff * bDiff;
    }

    /// <summary>
    /// Получение климатических данных (температура, влажность) для указанной позиции
    /// </summary>
    private float2 GetClimateData(float x, float z)
    {
        // Вычисляем координаты чанка
        int chunkSize = (int)terrainGenerator.ChunkSize;
        int chunkX = Mathf.FloorToInt(x / chunkSize);
        int chunkZ = Mathf.FloorToInt(z / chunkSize);

        Vector2Int key = new Vector2Int(chunkX, chunkZ);

        // Проверяем кеш
        if (climateCache.TryGetValue(key, out float2 cachedClimate))
        {
            return cachedClimate;
        }

        // Вычисляем климатические данные
        float sampleX = (x + biomeSeed) * temperatureNoiseScale + temperatureOffset.x;
        float sampleZ = (z + biomeSeed) * temperatureNoiseScale + temperatureOffset.y;

        float temperature = (noise.cnoise(new float2(sampleX, sampleZ)) + 1) * 0.5f;

        sampleX = (x + biomeSeed + 500) * humidityNoiseScale + humidityOffset.x;
        sampleZ = (z + biomeSeed + 500) * humidityNoiseScale + humidityOffset.y;

        float humidity = (noise.cnoise(new float2(sampleX, sampleZ)) + 1) * 0.5f;

        float2 climate = new float2(temperature, humidity);

        // Кешируем результат
        climateCache[key] = climate;

        return climate;
    }

    /// <summary>
    /// Вычисление смешивания между биомами для указанной позиции
    /// </summary>
    public BiomeBlendData GetBiomeBlendAt(Vector3 worldPosition, int blendQuality = 4)
    {
        // Если смешивание отключено, просто возвращаем основной биом
        if (biomeBlendStrength <= 0.01f || blendQuality <= 1)
        {
            BiomeData mainBiome = GetBiomeAt(worldPosition);
            return new BiomeBlendData(mainBiome);
        }

        // Количество точек для проверки вокруг указанной позиции
        int sampleCount = Mathf.Max(4, blendQuality * 2);

        Dictionary<BiomeData, float> biomeWeights = new Dictionary<BiomeData, float>();
        float totalWeight = 0f;

        // Проверяем точки вокруг указанной позиции
        float radius = terrainGenerator.ChunkSize * biomeBlendStrength;

        for (int i = 0; i < sampleCount; i++)
        {
            float angle = i * (2 * Mathf.PI / sampleCount);
            float sampleX = worldPosition.x + Mathf.Cos(angle) * radius;
            float sampleZ = worldPosition.z + Mathf.Sin(angle) * radius;

            BiomeData biome = GetBiomeAt(sampleX, sampleZ);

            // Вес обратно пропорционален расстоянию от центра
            float weight = 1f;

            if (!biomeWeights.ContainsKey(biome))
            {
                biomeWeights[biome] = weight;
            }
            else
            {
                biomeWeights[biome] += weight;
            }

            totalWeight += weight;
        }

        // Основной биом в центре имеет наибольший вес
        BiomeData centerBiome = GetBiomeAt(worldPosition);
        float centerWeight = totalWeight * 0.5f;

        if (!biomeWeights.ContainsKey(centerBiome))
        {
            biomeWeights[centerBiome] = centerWeight;
        }
        else
        {
            biomeWeights[centerBiome] += centerWeight;
        }

        totalWeight += centerWeight;

        // Нормализуем веса и создаем результат
        BiomeBlendData result = new BiomeBlendData();

        foreach (var pair in biomeWeights)
        {
            result.AddBiome(pair.Key, pair.Value / totalWeight);
        }

        return result;
    }

    /// <summary>
    /// Получение настроек шума для указанной позиции с учетом смешивания биомов
    /// </summary>
    public NoiseFunctions.NoiseSettings GetNoiseSettingsAt(Vector3 worldPosition)
    {
        BiomeBlendData blendData = GetBiomeBlendAt(worldPosition, biomeQuality);

        // Если только один биом, просто возвращаем его настройки
        if (blendData.BiomeCount == 1)
        {
            return blendData.MainBiome.noiseSettings;
        }

        // Смешиваем настройки шума
        NoiseFunctions.NoiseSettings blendedSettings = new NoiseFunctions.NoiseSettings();

        // Настройки выбираем от основного биома
        blendedSettings.noiseType = blendData.MainBiome.noiseSettings.noiseType;

        // Смешиваем числовые параметры
        blendedSettings.scale = 0;
        blendedSettings.octaves = 0;
        blendedSettings.persistence = 0;
        blendedSettings.lacunarity = 0;
        blendedSettings.offset = 0;

        for (int i = 0; i < blendData.BiomeCount; i++)
        {
            BiomeData biome = blendData.Biomes[i];
            float weight = blendData.Weights[i];

            blendedSettings.scale += biome.noiseSettings.scale * weight;
            blendedSettings.octaves += Mathf.RoundToInt(biome.noiseSettings.octaves * weight);
            blendedSettings.persistence += biome.noiseSettings.persistence * weight;
            blendedSettings.lacunarity += biome.noiseSettings.lacunarity * weight;
            blendedSettings.offset += biome.noiseSettings.offset * weight;
        }

        return blendedSettings;
    }

    /// <summary>
    /// Получение множителя высоты для указанной позиции с учетом смешивания биомов
    /// </summary>
    public float GetHeightMultiplierAt(Vector3 worldPosition)
    {
        BiomeBlendData blendData = GetBiomeBlendAt(worldPosition, biomeQuality);

        // Если только один биом, просто возвращаем его множитель
        if (blendData.BiomeCount == 1)
        {
            return blendData.MainBiome.heightMultiplier;
        }

        // Смешиваем множители высоты
        float heightMultiplier = 0;

        for (int i = 0; i < blendData.BiomeCount; i++)
        {
            heightMultiplier += blendData.Biomes[i].heightMultiplier * blendData.Weights[i];
        }

        return heightMultiplier;
    }

    /// <summary>
    /// Получение множителя склона для указанной позиции с учетом смешивания биомов
    /// </summary>
    public float GetSlopeMultiplierAt(Vector3 worldPosition)
    {
        BiomeBlendData blendData = GetBiomeBlendAt(worldPosition, biomeQuality);

        // Если только один биом, просто возвращаем его множитель
        if (blendData.BiomeCount == 1)
        {
            return blendData.MainBiome.slopeMultiplier;
        }

        // Смешиваем множители склона
        float slopeMultiplier = 0;

        for (int i = 0; i < blendData.BiomeCount; i++)
        {
            slopeMultiplier += blendData.Biomes[i].slopeMultiplier * blendData.Weights[i];
        }

        return slopeMultiplier;
    }

    /// <summary>
    /// Получение кривой высоты для указанной позиции с учетом смешивания биомов
    /// </summary>
    public AnimationCurve GetHeightCurveAt(Vector3 worldPosition)
    {
        BiomeBlendData blendData = GetBiomeBlendAt(worldPosition, biomeQuality);

        // Если только один биом, просто возвращаем его кривую
        if (blendData.BiomeCount == 1)
        {
            return blendData.MainBiome.heightCurve;
        }

        // В случае смешивания биомов возвращаем кривую от основного биома
        // т.к. невозможно корректно смешать кривые Безье в реальном времени
        return blendData.MainBiome.heightCurve;
    }

    /// <summary>
    /// Получение информации о текстурах для указанной позиции с учетом смешивания биомов
    /// </summary>
    public BiomeTextureInfo GetTextureInfoAt(Vector3 worldPosition)
    {
        BiomeBlendData blendData = GetBiomeBlendAt(worldPosition, biomeQuality);
        BiomeTextureInfo textureInfo = new BiomeTextureInfo();

        // Простой случай - один биом
        if (blendData.BiomeCount == 1)
        {
            textureInfo.TextureLayers = blendData.MainBiome.textureLayers;
            textureInfo.TextureScale = blendData.MainBiome.textureScale;
            return textureInfo;
        }

        // Смешиваем масштаб текстур
        float textureScale = 0;

        for (int i = 0; i < blendData.BiomeCount; i++)
        {
            textureScale += blendData.Biomes[i].textureScale * blendData.Weights[i];
        }

        textureInfo.TextureScale = textureScale;

        // Выбираем текстурные слои от основного биома
        // Смешивание слоев текстур делается в шейдере на основе карты влияния биомов
        textureInfo.TextureLayers = blendData.MainBiome.textureLayers;

        // Добавляем информацию о дополнительных биомах и их весах
        textureInfo.BlendBiomes = blendData.Biomes;
        textureInfo.BlendWeights = blendData.Weights;

        return textureInfo;
    }

    /// <summary>
    /// Генерация растительности для указанного чанка
    /// </summary>
    public void GenerateVegetationForChunk(GameObject chunk, int seed = 0)
    {
        if (chunk == null) return;

        // Получаем центр чанка
        Vector3 chunkCenter = chunk.transform.position + new Vector3(terrainGenerator.ChunkSize / 2, 0, terrainGenerator.ChunkSize / 2);

        // Получаем смешивание биомов для центра чанка
        BiomeBlendData blendData = GetBiomeBlendAt(chunkCenter, biomeQuality);

        // Для каждого биома в смеси
        for (int i = 0; i < blendData.BiomeCount; i++)
        {
            BiomeData biome = blendData.Biomes[i];
            float weight = blendData.Weights[i];

            // Пропускаем биомы с малым влиянием
            if (weight < 0.1f) continue;

            // Генерируем растительность для этого биома
            GenerateVegetationForBiome(chunk, biome, weight, seed);
        }
    }

    /// <summary>
    /// Генерация растительности для указанного биома в чанке
    /// </summary>
    private void GenerateVegetationForBiome(GameObject chunk, BiomeData biome, float biomeWeight, int seed)
    {
        if (biome.vegetation.Count == 0) return;

        // Получаем меш чанка
        MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        Mesh mesh = meshFilter.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        System.Random random = new System.Random(seed + biome.id.GetHashCode());

        // Максимальное количество растительности на один биом
        int maxVegetation = Mathf.RoundToInt(maxVegetationPerChunk * biomeWeight * biome.vegetationDensity);

        // Ограничиваем количество попыток размещения
        int placementAttempts = maxVegetation * 2;
        int placedCount = 0;

        // Размещаем растительность случайным образом на меше
        for (int attempt = 0; attempt < placementAttempts && placedCount < maxVegetation; attempt++)
        {
            // Выбираем случайный треугольник
            int triangleIndex = random.Next(0, triangles.Length / 3) * 3;

            // Получаем вершины треугольника
            Vector3 v1 = vertices[triangles[triangleIndex]];
            Vector3 v2 = vertices[triangles[triangleIndex + 1]];
            Vector3 v3 = vertices[triangles[triangleIndex + 2]];

            // Генерируем случайную точку на треугольнике
            float r1 = (float)random.NextDouble();
            float r2 = (float)random.NextDouble();

            if (r1 + r2 > 1)
            {
                r1 = 1 - r1;
                r2 = 1 - r2;
            }

            Vector3 localPosition = v1 * (1 - r1 - r2) + v2 * r1 + v3 * r2;

            // Вычисляем нормаль в этой точке
            Vector3 normal = Vector3.Cross(v2 - v1, v3 - v1).normalized;

            // Вычисляем наклон поверхности (0 - горизонтальная, 1 - вертикальная)
            float slope = 1 - Vector3.Dot(normal, Vector3.up);

            // Нормализуем высоту для проверки ограничений
            float normalizedHeight = Mathf.InverseLerp(0, biome.heightMultiplier, localPosition.y);

            // Для каждого типа растительности проверяем условия размещения
            foreach (var vegData in biome.vegetation)
            {
                // Проверка плотности
                if (random.NextDouble() > vegData.density * biomeWeight) continue;

                // Проверяем высоту
                if (normalizedHeight < vegData.minHeight || normalizedHeight > vegData.maxHeight) continue;

                // Проверяем наклон
                if (slope < vegData.minSlope || slope > vegData.maxSlope) continue;

                // Если все условия выполнены, размещаем растительность
                PlaceVegetation(chunk, vegData, localPosition, normal, random);
                placedCount++;
                break;
            }
        }
    }

    /// <summary>
    /// Размещение отдельного экземпляра растительности
    /// </summary>
    private void PlaceVegetation(GameObject chunk, VegetationData vegData, Vector3 localPosition, Vector3 normal, System.Random random)
    {
        // Мировая позиция
        Vector3 worldPosition = chunk.transform.TransformPoint(localPosition);
        worldPosition.y += vegData.heightOffset;

        // Создаем или получаем из пула префаб растительности
        GameObject vegInstance = CreateVegetationInstance(vegData, worldPosition);
        if (vegInstance == null) return;

        // Настраиваем размер
        float scale = Mathf.Lerp(vegData.minScale, vegData.maxScale, (float)random.NextDouble());
        vegInstance.transform.localScale = Vector3.one * scale;

        // Настраиваем поворот
        if (vegData.randomRotation)
        {
            float rotationY = (float)random.NextDouble() * 360f;
            vegInstance.transform.rotation = Quaternion.Euler(0, rotationY, 0);
        }

        // Выравниваем по нормали поверхности
        if (vegData.alignToNormal)
        {
            vegInstance.transform.up = normal;
        }

        // Устанавливаем родителя
        vegInstance.transform.SetParent(chunk.transform);

        // Добавляем в список размещенной растительности
        placedVegetation.Add(vegInstance);
    }

    /// <summary>
    /// Создание или получение из пула экземпляра растительности
    /// </summary>
    private GameObject CreateVegetationInstance(VegetationData vegData, Vector3 position)
    {
        GameObject prefab = null;

        // Сначала проверяем, есть ли префаб в кеше
        if (vegetationPrefabs.TryGetValue(vegData.prefabName, out prefab))
        {
            // Префаб найден в кеше
        }
        else if (vegData.prefab != null)
        {
            // Используем прямую ссылку на префаб
            prefab = vegData.prefab;
            vegetationPrefabs[vegData.prefabName] = prefab;
        }
        else
        {
            // Ищем префаб по имени в ресурсах
            prefab = Resources.Load<GameObject>($"Vegetation/{vegData.prefabName}");

            if (prefab == null)
            {
                Debug.LogWarning($"Префаб растительности не найден: {vegData.prefabName}");
                return null;
            }

            vegetationPrefabs[vegData.prefabName] = prefab;
        }

        // Если используем инстансинг, получаем экземпляр из пула
        if (useVegetationInstancing)
        {
            // Проверяем, есть ли пул для данного типа растительности
            if (!vegetationPools.TryGetValue(vegData.prefabName, out List<GameObject> pool))
            {
                pool = new List<GameObject>();
                vegetationPools[vegData.prefabName] = pool;
            }

            // Ищем неактивный экземпляр в пуле
            GameObject instance = pool.FirstOrDefault(v => !v.activeSelf);

            if (instance == null)
            {
                // Создаем новый экземпляр
                instance = Instantiate(prefab);
                pool.Add(instance);
            }

            // Настраиваем экземпляр
            instance.transform.position = position;
            instance.SetActive(true);

            return instance;
        }
        else
        {
            // Просто создаем новый экземпляр
            return Instantiate(prefab, position, Quaternion.identity);
        }
    }

    /// <summary>
    /// Обновление видимости растительности в зависимости от расстояния до камеры
    /// </summary>
    private void UpdateVegetationVisibility()
    {
        if (mainCamera == null) return;

        Vector3 cameraPos = mainCamera.transform.position;

        foreach (var vegetation in placedVegetation)
        {
            if (vegetation == null) continue;

            float distance = Vector3.Distance(vegetation.transform.position, cameraPos);
            bool isVisible = distance <= vegetationDrawDistance;

            if (vegetation.activeSelf != isVisible)
            {
                vegetation.SetActive(isVisible);
            }
        }
    }

    /// <summary>
    /// Очистка всей растительности
    /// </summary>
    public void ClearVegetation()
    {
        foreach (var vegetation in placedVegetation)
        {
            if (vegetation != null)
            {
                if (useVegetationInstancing)
                {
                    vegetation.SetActive(false);
                }
                else
                {
                    Destroy(vegetation);
                }
            }
        }

        placedVegetation.Clear();
    }

    /// <summary>
    /// Регенерация всей растительности для текущего ландшафта
    /// </summary>
    public void RegenerateVegetation()
    {
        ClearVegetation();

        // Получаем все чанки
        var chunks = terrainGenerator.Chunks;
        int seed = Mathf.RoundToInt(biomeSeed * 1000);

        foreach (var chunk in chunks)
        {
            GenerateVegetationForChunk(chunk, seed);
            seed++;
        }
    }

    /// <summary>
    /// Получение карты влияния биомов для шейдера
    /// </summary>
    public Texture2D GenerateBiomeInfluenceMap(int resolution = 256)
    {
        Texture2D influenceMap = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);

        float worldSize = terrainGenerator.ChunkSize * 16; // Примерный размер мира для карты
        float pixelSize = worldSize / resolution;

        biomeGridForGizmos = new Vector4[resolution, resolution];

        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float worldX = x * pixelSize - worldSize / 2;
                float worldZ = y * pixelSize - worldSize / 2;

                BiomeBlendData blendData = GetBiomeBlendAt(new Vector3(worldX, 0, worldZ), biomeQuality);

                // Определяем до 4-х основных биомов для каждой точки
                Color color = new Color(0, 0, 0, 0);
                Vector4 biomeIndices = new Vector4(-1, -1, -1, -1);

                for (int i = 0; i < Mathf.Min(4, blendData.BiomeCount); i++)
                {
                    int biomeIndex = biomes.IndexOf(blendData.Biomes[i]);
                    float weight = blendData.Weights[i];

                    // Записываем информацию о биоме и его весе
                    if (i == 0) { color.r = weight; biomeIndices.x = biomeIndex; }
                    else if (i == 1) { color.g = weight; biomeIndices.y = biomeIndex; }
                    else if (i == 2) { color.b = weight; biomeIndices.z = biomeIndex; }
                    else if (i == 3) { color.a = weight; biomeIndices.w = biomeIndex; }
                }

                influenceMap.SetPixel(x, y, color);
                biomeGridForGizmos[x, y] = biomeIndices;
            }
        }

        influenceMap.Apply();
        return influenceMap;
    }

    /// <summary>
    /// Обновление шейдера с информацией о биомах
    /// </summary>
    public void UpdateShaderWithBiomeData()
    {
        if (terrainGenerator.SharedMaterial == null)
        {
            Debug.LogWarning("TerrainGenerator.SharedMaterial не установлен!");
            return;
        }

        Material mat = terrainGenerator.SharedMaterial;

        // Обновляем информацию о биомах в шейдере
        mat.SetInt("biomeCount", biomes.Count);

        for (int i = 0; i < biomes.Count; i++)
        {
            // Устанавливаем данные о биоме
            mat.SetVector($"biomeData{i}", new Vector4(
                biomes[i].temperature,
                biomes[i].humidity,
                biomes[i].baseHeight,
                biomes[i].snowHeight
            ));
        }

        // Генерируем карту влияния биомов
        Texture2D influenceMap = GenerateBiomeInfluenceMap(256);
        mat.SetTexture("_BiomeInfluenceMap", influenceMap);

        // Устанавливаем параметры смешивания
        mat.SetFloat("_BiomeBlendStrength", biomeBlendStrength);
        mat.SetFloat("_BiomeMapScale", biomeMapScale);
    }

    /// <summary>
    /// Получение информации о всех биомах для интеграции с TerrainGenerator
    /// </summary>
    public BiomeInfo[] GetBiomeInfoArray()
    {
        BiomeInfo[] result = new BiomeInfo[biomes.Count];

        for (int i = 0; i < biomes.Count; i++)
        {
            result[i] = new BiomeInfo
            {
                BiomeData = biomes[i],
                Index = i
            };
        }

        return result;
    }

    /// <summary>
    /// Получение списка путей к текстурам для загрузки в шейдер
    /// </summary>
    public List<string> GetAllTextureNames()
    {
        HashSet<string> textureNames = new HashSet<string>();

        foreach (var biome in biomes)
        {
            foreach (var layer in biome.textureLayers)
            {
                textureNames.Add(layer.textureName);
            }
        }

        return textureNames.ToList();
    }

    private void OnDrawGizmos()
    {
        if (!showBiomeGizmos || !Application.isEditor) return;

        // Отображаем сетку биомов
        if (biomeGridForGizmos != null)
        {
            float worldSize = terrainGenerator.ChunkSize * 16;
            int resolution = biomeGridForGizmos.GetLength(0);
            float pixelSize = worldSize / resolution;

            for (int y = 0; y < resolution; y += 8)
            {
                for (int x = 0; x < resolution; x += 8)
                {
                    float worldX = x * pixelSize - worldSize / 2;
                    float worldZ = y * pixelSize - worldSize / 2;

                    Vector4 biomeIndices = biomeGridForGizmos[x, y];

                    if (biomeIndices.x >= 0 && biomeIndices.x < biomes.Count)
                    {
                        Color biomeColor = biomes[(int)biomeIndices.x].editorColor;
                        Gizmos.color = biomeColor;
                        Gizmos.DrawCube(new Vector3(worldX, 0, worldZ), Vector3.one * 2);
                    }
                }
            }
        }

        // Отображаем карту температуры
        if (showTemperatureMap && Application.isEditor)
        {
            float size = 4f;
            float step = 16f;

            for (float x = -128; x <= 128; x += step)
            {
                for (float z = -128; z <= 128; z += step)
                {
                    Vector3 pos = new Vector3(x, 0, z);
                    float2 climate = GetClimateData(x, z);

                    // Используем температуру для цвета (синий - холодный, красный - горячий)
                    Color tempColor = Color.Lerp(Color.blue, Color.red, climate.x);
                    Gizmos.color = tempColor;
                    Gizmos.DrawCube(pos, new Vector3(size, 0.1f, size));
                }
            }
        }

        // Отображаем карту влажности
        if (showHumidityMap && Application.isEditor)
        {
            float size = 4f;
            float step = 16f;
            float heightOffset = 4f; // Смещение вверх, чтобы не перекрывать карту температуры

            for (float x = -128; x <= 128; x += step)
            {
                for (float z = -128; z <= 128; z += step)
                {
                    Vector3 pos = new Vector3(x, heightOffset, z);
                    float2 climate = GetClimateData(x, z);

                    // Используем влажность для цвета (желтый - сухой, синий - влажный)
                    Color humidityColor = Color.Lerp(Color.yellow, Color.blue, climate.y);
                    Gizmos.color = humidityColor;
                    Gizmos.DrawCube(pos, new Vector3(size, 0.1f, size));
                }
            }
        }
    }

    [BurstCompile]
    public struct ClimateCalculationJob : IJobParallelFor
    {
        // Параметры задачи
        public int XStart;
        public int ZStart;
        public int Width;
        public int Height;
        public float TempNoiseScale;
        public float HumidityNoiseScale;
        public float2 TemperatureOffset;
        public float2 HumidityOffset;
        public int ChunkSize;
        public float Seed;

        // Выходные данные
        public NativeArray<float2> Results;

        public void Execute(int index)
        {
            int z = index / Width;
            int x = index % Width;

            int worldX = XStart + x;
            int worldZ = ZStart + z;

            float sampleX = (worldX * ChunkSize + Seed) * TempNoiseScale + TemperatureOffset.x;
            float sampleZ = (worldZ * ChunkSize + Seed) * TempNoiseScale + TemperatureOffset.y;

            float temperature = (noise.cnoise(new float2(sampleX, sampleZ)) + 1) * 0.5f;

            sampleX = (worldX * ChunkSize + Seed + 500) * HumidityNoiseScale + HumidityOffset.x;
            sampleZ = (worldZ * ChunkSize + Seed + 500) * HumidityNoiseScale + HumidityOffset.y;

            float humidity = (noise.cnoise(new float2(sampleX, sampleZ)) + 1) * 0.5f;

            Results[index] = new float2(temperature, humidity);
        }
    }
}

/// <summary>
/// Структура для хранения информации о смешивании биомов
/// </summary>
public class BiomeBlendData
{
    private List<BiomeData> biomes = new List<BiomeData>();
    private List<float> weights = new List<float>();

    public List<BiomeData> Biomes => biomes;
    public List<float> Weights => weights;
    public int BiomeCount => biomes.Count;

    public BiomeData MainBiome => biomes.Count > 0 ? biomes[0] : null;

    public BiomeBlendData()
    {
    }

    public BiomeBlendData(BiomeData singleBiome)
    {
        if (singleBiome != null)
        {
            biomes.Add(singleBiome);
            weights.Add(1.0f);
        }
    }

    public void AddBiome(BiomeData biome, float weight)
    {
        if (biome == null || weight <= 0f)
            return;

        // Проверяем, есть ли уже такой биом
        int index = biomes.IndexOf(biome);

        if (index >= 0)
        {
            // Суммируем вес
            weights[index] += weight;
        }
        else
        {
            biomes.Add(biome);
            weights.Add(weight);
        }

        // Сортируем по весу
        SortByWeight();
    }

    private void SortByWeight()
    {
        // Создаем список пар (биом, вес)
        List<(BiomeData biome, float weight)> pairs = new List<(BiomeData, float)>();

        for (int i = 0; i < biomes.Count; i++)
        {
            pairs.Add((biomes[i], weights[i]));
        }

        // Сортируем по убыванию веса
        pairs.Sort((a, b) => b.weight.CompareTo(a.weight));

        // Обновляем списки
        biomes.Clear();
        weights.Clear();

        foreach (var pair in pairs)
        {
            biomes.Add(pair.biome);
            weights.Add(pair.weight);
        }
    }
}

/// <summary>
/// Информация о текстурах для биома
/// </summary>
public class BiomeTextureInfo
{
    public List<TerrainTextureLayer> TextureLayers { get; set; }
    public float TextureScale { get; set; }
    public List<BiomeData> BlendBiomes { get; set; }
    public List<float> BlendWeights { get; set; }
}

/// <summary>
/// Информация о биоме для интеграции с TerrainGenerator
/// </summary>
public class BiomeInfo
{
    public BiomeData BiomeData { get; set; }
    public int Index { get; set; }
}