using UnityEngine;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using System.Linq;
using System.Threading.Tasks;
using TerrainGeneration;
using System.Collections.Concurrent;

/// <summary>
/// BiomeGenerator handles the integration of biomes with the terrain generation system.
/// It provides functionality for applying biome data to terrain, managing textures,
/// and handling vegetation placement.
/// </summary>
public class BiomeGenerator : MonoBehaviour
{
    [SerializeField] private TerrainGenerator terrainGenerator;
    [SerializeField] private BiomeManager biomeManager;

    [Header("Integration with TerrainGenerator")]
    [Tooltip("Automatic integration with TerrainGenerator")]
    [SerializeField] private bool autoIntegrate = true;

    [Tooltip("Generate noise based on biomes")]
    [SerializeField] private bool useTerrainGeneratorJobs = true;

    [Tooltip("Apply biome textures to shader")]
    [SerializeField] private bool updateMaterialTextures = true;

    [Header("Generation Settings")]
    [Tooltip("Heightmap resolution for biomes")]
    [SerializeField] private int heightmapResolution = 1024;

    [Tooltip("Use multithreaded generation")]
    [SerializeField] private bool useMultithreading = true;

    [Tooltip("Biome influence on base terrain height")]
    [Range(0f, 1f)]
    [SerializeField] private float biomeHeightInfluence = 0.5f;

    [Tooltip("Biome influence on terrain shape")]
    [Range(0f, 1f)]
    [SerializeField] private float biomeShapeInfluence = 0.7f;

    [Header("Caching and Performance")]
    [Tooltip("Use height caching")]
    [SerializeField] private bool useHeightCaching = true;

    [Tooltip("Maximum height cache size (in megabytes)")]
    [SerializeField] private int maxHeightCacheMB = 256;

    [Header("Vegetation")]
    [Tooltip("Automatically place vegetation")]
    [SerializeField] private bool autoPlaceVegetation = true;

    [Tooltip("Maximum vegetation objects")]
    [SerializeField] private int maxVegetationObjects = 10000;

    [Header("Mobile Optimization")]
    [Tooltip("Low power mode for mobile devices")]
    [SerializeField] private bool mobileLowPowerMode = false;

    [Tooltip("Polygon reduction for mobile devices")]
    [Range(0.1f, 1f)]
    [SerializeField] private float mobilePolyReduction = 1f;

    // Private variables
    private Texture2DArray biomeTextureArray;
    private Texture2DArray biomeNormalArray;
    private Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>(32);
    private Dictionary<Vector2Int, float> heightCache = new Dictionary<Vector2Int, float>(10000);
    private bool isInitialized = false;
    private bool isGenerating = false;
    private int initRetryCount = 0;
    private float lastCacheClearTime;
    private const float CACHE_CLEAR_INTERVAL = 120f; // 2 minutes

    // System information
    private bool isMobilePlatform = false;
    private int systemMemoryMB = 0;
    private bool isLowEndDevice = false;

    private void Awake()
    {
        // Check platform and system specs
        CheckSystemSpecs();

        // Try to initialize components
        TryInitialize();

        // Set cache clear time
        lastCacheClearTime = Time.time;
    }

    private void Start()
    {
        if (!isInitialized)
        {
            // Retry initialization
            TryInitialize();
        }
    }

    private void OnEnable()
    {
        // Subscribe to terrain generator events
        if (terrainGenerator != null)
        {
            // Subscribe to events if they are added to TerrainGenerator
        }
    }

    private void OnDisable()
    {
        // Unsubscribe from events
        if (terrainGenerator != null)
        {
            // Unsubscribe from events
        }
    }

    private void Update()
    {
        if (!isInitialized && initRetryCount < 3)
        {
            TryInitialize();
            initRetryCount++;
        }

        // Periodically clean height cache to prevent memory bloat
        if (Time.time - lastCacheClearTime > CACHE_CLEAR_INTERVAL && heightCache.Count > 5000)
        {
            CleanHeightCache();
            lastCacheClearTime = Time.time;
        }
    }

    /// <summary>
    /// Checks system specifications for optimization
    /// </summary>
    private void CheckSystemSpecs()
    {
        // Determine device type
        isMobilePlatform = Application.isMobilePlatform;

        // Determine available memory
        systemMemoryMB = SystemInfo.systemMemorySize;

        // Check if device is low-end
        isLowEndDevice = SystemInfo.graphicsMemorySize < 1024 || systemMemoryMB < 2048;

        // Automatic settings adjustment for mobile devices
        if (isMobilePlatform || isLowEndDevice)
        {
            mobileLowPowerMode = true;
            mobilePolyReduction = Mathf.Clamp(0.5f, 0.1f, 1f);
            useMultithreading = SystemInfo.processorCount > 2;
            heightmapResolution = Mathf.Min(heightmapResolution, 512);
            maxHeightCacheMB = Mathf.Min(maxHeightCacheMB, 64);
        }
    }

    /// <summary>
    /// Attempts to initialize required components
    /// </summary>
    private void TryInitialize()
    {
        if (isInitialized) return;

        // Check for TerrainGenerator
        if (terrainGenerator == null)
        {
            terrainGenerator = GetComponent<TerrainGenerator>();

            if (terrainGenerator == null)
            {
                terrainGenerator = UnityEngine.Object.FindFirstObjectByType<TerrainGenerator>();

                if (terrainGenerator == null)
                {
                    Debug.LogError("BiomeGenerator: TerrainGenerator not found!");
                    return;
                }
            }
        }

        // Check for BiomeManager
        if (biomeManager == null)
        {
            biomeManager = GetComponent<BiomeManager>();

            if (biomeManager == null)
            {
                biomeManager = UnityEngine.Object.FindFirstObjectByType<BiomeManager>();

                if (biomeManager == null)
                {
                    Debug.LogError("BiomeGenerator: BiomeManager not found!");
                    return;
                }
            }
        }

        // Integrate with terrain generator if needed
        if (autoIntegrate)
        {
            IntegrateWithTerrainGenerator();
        }

        // Initialize texture arrays
        if (updateMaterialTextures)
        {
            InitializeTextureArrays();
            GenerateTextureArrays();
        }

        isInitialized = true;
        Debug.Log("BiomeGenerator: Initialization completed successfully.");
    }

    /// <summary>
    /// Integrates with terrain generator
    /// </summary>
    private void IntegrateWithTerrainGenerator()
    {
        // This method should be called after TerrainGenerator and BiomeManager initialization
        if (terrainGenerator == null || biomeManager == null) return;

        // Update material with biome information
        if (terrainGenerator.SharedMaterial != null)
        {
            biomeManager.UpdateShaderWithBiomeData();

            // Set parameters for low-performance devices
            if (mobileLowPowerMode)
            {
                ApplyMobileOptimizations();
            }
        }

        // Automatically place vegetation
        if (autoPlaceVegetation)
        {
            biomeManager.RegenerateVegetation();
        }
    }

    /// <summary>
    /// Applies optimizations for mobile devices
    /// </summary>
    private void ApplyMobileOptimizations()
    {
        if (terrainGenerator.SharedMaterial != null)
        {
            // Disable complex shader effects for mobile devices
            terrainGenerator.SharedMaterial.SetFloat("_DetailBumpScale", 0.1f);
            terrainGenerator.SharedMaterial.SetFloat("_SlopeNoiseStrength", 0.05f);

            // Reduce texture resolution
            terrainGenerator.SharedMaterial.SetFloat("_TextureScale", terrainGenerator.SharedMaterial.GetFloat("_TextureScale") * 2);
        }
    }

    /// <summary>
    /// Initializes texture arrays
    /// </summary>
    private void InitializeTextureArrays()
    {
        // Get list of all textures from biomes
        List<string> allTextureNames = biomeManager.GetAllTextureNames();

        // Create texture arrays
        biomeTextureArray = new Texture2DArray(512, 512, allTextureNames.Count, TextureFormat.RGBA32, true);
        biomeNormalArray = new Texture2DArray(512, 512, allTextureNames.Count, TextureFormat.RGBA32, true);

        // Set filtering
        biomeTextureArray.filterMode = FilterMode.Bilinear;
        biomeNormalArray.filterMode = FilterMode.Bilinear;

        // Set wrap mode
        biomeTextureArray.wrapMode = TextureWrapMode.Repeat;
        biomeNormalArray.wrapMode = TextureWrapMode.Repeat;
    }

    /// <summary>
    /// Generates and populates texture arrays
    /// </summary>
    private void GenerateTextureArrays()
    {
        // Get list of all textures from biomes
        List<string> allTextureNames = biomeManager.GetAllTextureNames();

        // Process textures in parallel for better performance
        if (useMultithreading && SystemInfo.processorCount > 2)
        {
            Parallel.ForEach(Partitioner.Create(0, allTextureNames.Count), range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    PrepareTextureForArray(allTextureNames[i], i);
                }
            });
        }
        else
        {
            for (int i = 0; i < allTextureNames.Count; i++)
            {
                PrepareTextureForArray(allTextureNames[i], i);
            }
        }

        // Apply changes
        biomeTextureArray.Apply();
        biomeNormalArray.Apply();

        // Set texture arrays in shader
        if (terrainGenerator.SharedMaterial != null)
        {
            terrainGenerator.SharedMaterial.SetTexture("terrainTextures", biomeTextureArray);
            terrainGenerator.SharedMaterial.SetTexture("terrainNormals", biomeNormalArray);
        }
    }

    /// <summary>
    /// Prepares a texture for array insertion
    /// </summary>
    private void PrepareTextureForArray(string textureName, int index)
    {
        // Load texture and normal
        Texture2D texture = LoadTexture(textureName);
        Texture2D normal = LoadNormalMap(textureName);

        if (texture != null)
        {
            // Scale texture to needed size if necessary
            if (texture.width != 512 || texture.height != 512)
            {
                texture = ScaleTexture(texture, 512, 512);
            }

            // Copy texture to array
            lock (biomeTextureArray)
            {
                Graphics.CopyTexture(texture, 0, 0, biomeTextureArray, index, 0);
            }

            // If this is a temporary texture, delete it
            if (!textureCache.ContainsKey(textureName))
            {
                Destroy(texture);
            }
        }
        else
        {
            // If texture is not found, use dummy texture
            texture = CreateDummyTexture(textureName);
            lock (biomeTextureArray)
            {
                Graphics.CopyTexture(texture, 0, 0, biomeTextureArray, index, 0);
            }
            Destroy(texture);
        }

        if (normal != null)
        {
            // Scale normal to needed size if necessary
            if (normal.width != 512 || normal.height != 512)
            {
                normal = ScaleTexture(normal, 512, 512);
            }

            // Copy normal to array
            lock (biomeNormalArray)
            {
                Graphics.CopyTexture(normal, 0, 0, biomeNormalArray, index, 0);
            }

            // If this is a temporary texture, delete it
            if (!textureCache.ContainsKey(textureName + "_Normal"))
            {
                Destroy(normal);
            }
        }
        else
        {
            // If normal map is not found, use default
            normal = CreateDefaultNormalMap();
            lock (biomeNormalArray)
            {
                Graphics.CopyTexture(normal, 0, 0, biomeNormalArray, index, 0);
            }
            Destroy(normal);
        }
    }

    /// <summary>
    /// Loads a texture by name
    /// </summary>
    private Texture2D LoadTexture(string textureName)
    {
        // First check cache
        if (textureCache.TryGetValue(textureName, out Texture2D cachedTexture))
        {
            return cachedTexture;
        }

        // Look for texture in resources
        Texture2D texture = Resources.Load<Texture2D>($"Textures/{textureName}");

        if (texture != null)
        {
            // Add to cache
            textureCache[textureName] = texture;
            return texture;
        }

        // Try to find in alternative paths
        texture = Resources.Load<Texture2D>(textureName);

        if (texture != null)
        {
            textureCache[textureName] = texture;
            return texture;
        }

        return null;
    }

    /// <summary>
    /// Loads a normal map by texture name
    /// </summary>
    private Texture2D LoadNormalMap(string textureName)
    {
        string normalMapName = textureName + "_Normal";

        // First check cache
        if (textureCache.TryGetValue(normalMapName, out Texture2D cachedNormal))
        {
            return cachedNormal;
        }

        // Look for normal map in resources
        Texture2D normal = Resources.Load<Texture2D>($"Textures/{normalMapName}");

        if (normal != null)
        {
            // Add to cache
            textureCache[normalMapName] = normal;
            return normal;
        }

        // Try to find in alternative paths
        normal = Resources.Load<Texture2D>(normalMapName);

        if (normal != null)
        {
            textureCache[normalMapName] = normal;
            return normal;
        }

        return null;
    }

    /// <summary>
    /// Creates a dummy texture with name as debug information
    /// </summary>
    private Texture2D CreateDummyTexture(string textureName)
    {
        // Create texture of specified size
        Texture2D dummyTexture = new Texture2D(512, 512, TextureFormat.RGBA32, false);

        // Create color based on name hash
        int hash = textureName.GetHashCode();
        Color baseColor = new Color(
            (hash & 0xFF) / 255f,
            ((hash >> 8) & 0xFF) / 255f,
            ((hash >> 16) & 0xFF) / 255f,
            1f
        );

        // Fill texture with base color using checkered pattern
        Color[] pixels = new Color[512 * 512];

        for (int y = 0; y < 512; y++)
        {
            for (int x = 0; x < 512; x++)
            {
                bool isAlternateColor = ((x / 64) + (y / 64)) % 2 == 0;
                pixels[y * 512 + x] = isAlternateColor ? baseColor : baseColor * 0.8f;
            }
        }

        dummyTexture.SetPixels(pixels);
        dummyTexture.Apply();

        return dummyTexture;
    }

    /// <summary>
    /// Creates a default normal map (blue)
    /// </summary>
    private Texture2D CreateDefaultNormalMap()
    {
        // Create texture of specified size
        Texture2D normalMap = new Texture2D(512, 512, TextureFormat.RGBA32, false);

        // Color for normal map pointing up (0.5, 0.5, 1, 1)
        Color normalColor = new Color(0.5f, 0.5f, 1f, 1f);

        // Fill texture
        Color[] pixels = new Color[512 * 512];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = normalColor;
        }

        normalMap.SetPixels(pixels);
        normalMap.Apply();

        return normalMap;
    }

    /// <summary>
    /// Scales a texture to the specified size
    /// </summary>
    private Texture2D ScaleTexture(Texture2D source, int targetWidth, int targetHeight)
    {
        // Create RenderTexture for scaling
        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight);

        // Copy source texture with scaling
        Graphics.Blit(source, rt);

        // Create new texture of target size
        Texture2D result = new Texture2D(targetWidth, targetHeight);

        // Activate RenderTexture and copy pixels
        RenderTexture.active = rt;
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        result.Apply();

        // Clean up
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);

        return result;
    }

    /// <summary>
    /// Gets biome height for the specified position with caching
    /// </summary>
    public float GetBiomeHeightAt(float x, float z)
    {
        if (biomeManager == null) return 0f;

        // Round coordinates to reduce cache size
        int roundX = Mathf.RoundToInt(x);
        int roundZ = Mathf.RoundToInt(z);
        Vector2Int key = new Vector2Int(roundX, roundZ);

        // Check cache if caching is enabled
        if (useHeightCaching && heightCache.TryGetValue(key, out float cachedHeight))
        {
            return cachedHeight;
        }

        // Get biome information
        Vector3 position = new Vector3(x, 0, z);
        BiomeData biome = biomeManager.GetBiomeAt(position);

        if (biome == null) return 0f;

        // Get noise parameters
        NoiseFunctions.NoiseSettings noiseSettings = biomeManager.GetNoiseSettingsAt(position);
        float heightMultiplier = biomeManager.GetHeightMultiplierAt(position);

        // Calculate noise value
        float noiseValue = 0f;
        float amplitude = 1f;
        float frequency = 1f;

        for (int i = 0; i < noiseSettings.octaves; i++)
        {
            float sampleX = (x * frequency) * noiseSettings.scale;
            float sampleZ = (z * frequency) * noiseSettings.scale;

            float perlinValue = 0f;

            // Choose noise type based on biome settings
            switch (noiseSettings.noiseType)
            {
                case NoiseFunctions.NoiseType.Perlin:
                    perlinValue = NoiseFunctions.PerlinNoise(sampleX, sampleZ);
                    break;
                case NoiseFunctions.NoiseType.Simplex:
                    perlinValue = NoiseFunctions.SimplexNoise(sampleX, sampleZ);
                    break;
                case NoiseFunctions.NoiseType.Value:
                    perlinValue = NoiseFunctions.ValueNoise(sampleX, sampleZ);
                    break;
                case NoiseFunctions.NoiseType.Ridged:
                    perlinValue = NoiseFunctions.RidgeNoise(sampleX, sampleZ);
                    break;
                case NoiseFunctions.NoiseType.FBM:
                    perlinValue = NoiseFunctions.FractalBrownianMotion(sampleX, sampleZ, 1, 2f, 0.5f);
                    break;
                case NoiseFunctions.NoiseType.Cellular:
                    perlinValue = NoiseFunctions.CellularNoise(sampleX, sampleZ, 10, 0, 0, 16);
                    break;
                default:
                    perlinValue = NoiseFunctions.PerlinNoise(sampleX, sampleZ);
                    break;
            }

            noiseValue += perlinValue * amplitude;
            amplitude *= noiseSettings.persistence;
            frequency *= noiseSettings.lacunarity;
        }

        // Apply height curve
        AnimationCurve heightCurve = biomeManager.GetHeightCurveAt(position);
        float normalizedNoiseValue = Mathf.Clamp01(noiseValue * 0.5f + 0.5f);
        float curvedValue = heightCurve.Evaluate(normalizedNoiseValue);

        // Calculate final height
        float height = curvedValue * heightMultiplier;

        // Add to cache if caching is enabled
        if (useHeightCaching)
        {
            // Check cache size to avoid memory leaks
            if (heightCache.Count > maxHeightCacheMB * 1024 * 256) // ~4 bytes per entry
            {
                // Clear part of cache
                int removeCount = heightCache.Count / 4;
                int i = 0;
                var keysToRemove = new List<Vector2Int>();

                foreach (var cacheKey in heightCache.Keys)
                {
                    if (i < removeCount)
                    {
                        keysToRemove.Add(cacheKey);
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                foreach (var keyToRemove in keysToRemove)
                {
                    heightCache.Remove(keyToRemove);
                }
            }

            heightCache[key] = height;
        }

        return height;
    }

    /// <summary>
    /// Generates a heightmap based on biomes
    /// </summary>
    public void GenerateBiomeHeightmap(int resolution = 1024)
    {
        if (biomeManager == null) return;

        // Create jobs for multithreaded generation
        if (useMultithreading)
        {
            GenerateHeightmapMultithreaded(resolution);
        }
        else
        {
            GenerateHeightmapSingleThreaded(resolution);
        }
    }

    /// <summary>
    /// Single-threaded heightmap generation
    /// </summary>
    private void GenerateHeightmapSingleThreaded(int resolution)
    {
        // Create heights array
        float[,] heightmap = new float[resolution, resolution];

        // World size
        float worldSize = terrainGenerator.ChunkSize * 16;
        float step = worldSize / resolution;

        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float worldX = x * step - worldSize / 2;
                float worldZ = z * step - worldSize / 2;

                heightmap[z, x] = GetBiomeHeightAt(worldX, worldZ);
            }
        }

        // Here you can apply heightmap to your terrain
    }

    /// <summary>
    /// Multi-threaded heightmap generation using Job System
    /// </summary>
    private void GenerateHeightmapMultithreaded(int resolution)
    {
        if (isGenerating) return;
        isGenerating = true;

        // Create Native arrays for Job System
        NativeArray<float> heights = new NativeArray<float>(resolution * resolution, Allocator.TempJob);

        // World size
        float worldSize = terrainGenerator.ChunkSize * 16;

        // Create job for multithreaded generation
        var heightmapJob = new HeightmapGenerationJob
        {
            Resolution = resolution,
            WorldSize = worldSize,
            BiomeCount = biomeManager.GetBiomeInfoArray().Length,
            Heights = heights
        };

        // Schedule job
        JobHandle jobHandle = heightmapJob.Schedule(resolution * resolution, 64);

        // Wait for job to complete
        jobHandle.Complete();

        // Copy results to regular array
        float[,] heightmap = new float[resolution, resolution];
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int index = z * resolution + x;
                heightmap[z, x] = heights[index];
            }
        }

        // Free resources
        heights.Dispose();

        // Here you can apply heightmap to your terrain
        isGenerating = false;
    }

    // Job for multithreaded heightmap generation
    [BurstCompile]
    private struct HeightmapGenerationJob : IJobParallelFor
    {
        public int Resolution;
        public float WorldSize;
        public int BiomeCount;

        public NativeArray<float> Heights;

        public void Execute(int index)
        {
            int z = index / Resolution;
            int x = index % Resolution;

            float step = WorldSize / Resolution;
            float worldX = x * step - WorldSize / 2;
            float worldZ = z * step - WorldSize / 2;

            // Ideally here should be the same algorithm as in GetBiomeHeightAt,
            // but since we can't call MonoBehaviour methods from Job,
            // need to implement logic directly here

            // Simplified version for example
            float perlinValue = noise.cnoise(new float2(worldX * 0.03f, worldZ * 0.03f));
            Heights[index] = perlinValue * 10f;
        }
    }

    /// <summary>
    /// Modifies TerrainGenerator settings to account for biomes
    /// </summary>
    public void ModifyTerrainGeneratorSettings()
    {
        if (terrainGenerator == null || biomeManager == null) return;

        // Here you can dynamically change TerrainGenerator settings
        // to account for different biomes. For example:

        // Set average height multiplier from all biomes
        var biomes = biomeManager.GetBiomeInfoArray();
        float avgHeightMultiplier = 0f;

        foreach (var biomeInfo in biomes)
        {
            avgHeightMultiplier += biomeInfo.BiomeData.heightMultiplier;
        }

        if (biomes.Length > 0)
        {
            avgHeightMultiplier /= biomes.Length;

            // Set this multiplier in TerrainGenerator (if there is a corresponding field)
            // terrainGenerator.heightMultipler = avgHeightMultiplier;
        }
    }

    /// <summary>
    /// Applies biome parameters to a terrain chunk
    /// </summary>
    public void ApplyBiomesToChunk(GameObject chunk)
    {
        if (chunk == null || biomeManager == null) return;

        // Get chunk center
        Vector3 chunkCenter = chunk.transform.position + new Vector3(terrainGenerator.ChunkSize / 2, 0, terrainGenerator.ChunkSize / 2);

        // Get biome for this chunk
        BiomeData biome = biomeManager.GetBiomeAt(chunkCenter);

        if (biome == null) return;

        // Apply biome settings to chunk shader
        MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
        if (renderer != null && renderer.sharedMaterial != null)
        {
            // Set parameters in MaterialPropertyBlock for optimization
            MaterialPropertyBlock props = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(props);

            // Set biome data
            props.SetVector("_BiomeData", new Vector4(
                biome.temperature,
                biome.humidity,
                biome.baseHeight,
                biome.snowHeight
            ));

            // Set Texture Scale from biome
            props.SetFloat("_TextureScale", biome.textureScale);

            renderer.SetPropertyBlock(props);
        }

        // Place vegetation if needed
        if (autoPlaceVegetation)
        {
            biomeManager.GenerateVegetationForChunk(chunk);
        }
    }

    /// <summary>
    /// Clears the height cache
    /// </summary>
    public void CleanHeightCache()
    {
        if (heightCache.Count > maxHeightCacheMB * 512) // Set reasonable threshold
        {
            // Keep only recent entries (e.g., 25%)
            int keepCount = heightCache.Count / 4;
            var keys = heightCache.Keys.ToList();

            // Remove oldest entries
            for (int i = 0; i < keys.Count - keepCount; i++)
            {
                heightCache.Remove(keys[i]);
            }
        }
    }

    /// <summary>
    /// Updates biome influence map in shader
    /// </summary>
    public void UpdateBiomeInfluenceMap()
    {
        biomeManager?.UpdateShaderWithBiomeData();
    }

    private void OnDestroy()
    {
        // Clean up resources
        if (biomeTextureArray != null)
        {
            Destroy(biomeTextureArray);
            biomeTextureArray = null;
        }

        if (biomeNormalArray != null)
        {
            Destroy(biomeNormalArray);
            biomeNormalArray = null;
        }

        // Clear caches
        heightCache.Clear();
        textureCache.Clear();
    }

    // Debug methods for editor display
    private void OnDrawGizmos()
    {
        if (!Application.isEditor) return;

        // Here you can add biome visualization in editor
    }
}