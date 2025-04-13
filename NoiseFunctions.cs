using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using Unity.Jobs;

/// <summary>
/// Static class providing various noise generation functions optimized for performance
/// </summary>
public static class NoiseFunctions
{
    // Cache for frequently used values to minimize recomputation
    private static NativeArray<float> SinCosCache;
    private static NativeArray<float> PersistenceCache;
    private static NativeArray<float> LacunarityCache;
    private static bool isInitialized = false;

    // Initialize caches
    public static void Initialize()
    {
        if (isInitialized) return;

        SinCosCache = new NativeArray<float>(1024, Allocator.Persistent);
        PersistenceCache = new NativeArray<float>(32, Allocator.Persistent);
        LacunarityCache = new NativeArray<float>(32, Allocator.Persistent);

        // Initialize sin/cos cache for value noise
        for (int i = 0; i < SinCosCache.Length; i++)
        {
            SinCosCache[i] = math.sin(i * 0.1f) * 43758.5453f;
        }

        // Initialize persistence and lacunarity caches
        for (int i = 0; i < PersistenceCache.Length; i++)
        {
            PersistenceCache[i] = math.pow(0.5f, i);  // Default persistence value
            LacunarityCache[i] = math.pow(2.0f, i);   // Default lacunarity value
        }

        isInitialized = true;
    }

    public static void Dispose()
    {
        if (SinCosCache.IsCreated) SinCosCache.Dispose();
        if (PersistenceCache.IsCreated) PersistenceCache.Dispose();
        if (LacunarityCache.IsCreated) LacunarityCache.Dispose();
        isInitialized = false;
    }

    /// <summary>
    /// Perlin Noise implementation
    /// </summary>
    [BurstCompile]
    public static float PerlinNoise(float x, float y)
    {
        return noise.cnoise(new float2(x, y));
    }

    /// <summary>
    /// Simplex Noise implementation
    /// </summary>
    [BurstCompile]
    public static float SimplexNoise(float x, float y)
    {
        return noise.snoise(new float2(x, y)) * 0.5f + 0.5f; // Convert to range [0, 1]
    }

    /// <summary>
    /// Value Noise implementation
    /// </summary>
    [BurstCompile]
    public static float ValueNoise(float x, float y)
    {
        int ix = (int)math.floor(x);
        int iy = (int)math.floor(y);
        float fx = x - ix;
        float fy = y - iy;

        // Use smoother interpolation
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);

        float v1 = RandomValue(ix, iy);
        float v2 = RandomValue(ix + 1, iy);
        float v3 = RandomValue(ix, iy + 1);
        float v4 = RandomValue(ix + 1, iy + 1);

        float i1 = math.lerp(v1, v2, fx);
        float i2 = math.lerp(v3, v4, fx);

        return math.lerp(i1, i2, fy);
    }

    /// <summary>
    /// Ridge Noise implementation
    /// </summary>
    [BurstCompile]
    public static float RidgeNoise(float x, float y)
    {
        float noiseValue = noise.cnoise(new float2(x, y));
        noiseValue = 1 - math.abs(noiseValue);
        noiseValue = math.pow(noiseValue, 2);
        return noiseValue;
    }

    /// <summary>
    /// Fractal Brownian Motion (FBM) implementation
    /// </summary>
    [BurstCompile]
    public static float FractalBrownianMotion(float x, float y, int octaves, float lacunarity, float persistence)
    {
        float value = 0;
        float amplitude = 1;
        float frequency = 1;

        for (int i = 0; i < octaves; i++)
        {
            float noiseValue = noise.cnoise(new float2(x * frequency, y * frequency));
            value += noiseValue * amplitude;

            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return value;
    }

    /// <summary>
    /// Domain Warping implementation
    /// </summary>
    [BurstCompile]
    public static float DomainWarping(float x, float y)
    {
        float warpX = noise.cnoise(new float2(x + 0.5f, y + 0.5f)) * 2 - 1;
        float warpY = noise.cnoise(new float2(x - 0.5f, y - 0.5f)) * 2 - 1;
        return noise.cnoise(new float2(x + warpX, y + warpY));
    }

    /// <summary>
    /// Cellular Noise optimized for Burst compiler
    /// </summary>
    [BurstCompile]
    public static float CellularNoise(float x, float y, int numCells, int chunkX, int chunkZ, int chunkSize)
    {
        float minDist1 = float.MaxValue;
        float minDist2 = float.MaxValue;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                int globalChunkX = chunkX + dx;
                int globalChunkZ = chunkZ + dz;

                for (int i = 0; i < numCells; i++)
                {
                    float cellX = RandomValue(globalChunkX * chunkSize + i, globalChunkZ * chunkSize);
                    float cellZ = RandomValue(globalChunkZ * chunkSize + i, globalChunkX * chunkSize);

                    cellX += globalChunkX * chunkSize;
                    cellZ += globalChunkZ * chunkSize;

                    float distX = x - cellX;
                    float distY = y - cellZ;
                    float distance = distX * distX + distY * distY;

                    if (distance < minDist1)
                    {
                        minDist2 = minDist1;
                        minDist1 = distance;
                    }
                    else if (distance < minDist2)
                    {
                        minDist2 = distance;
                    }
                }
            }
        }

        return math.sqrt(minDist2) - math.sqrt(minDist1);
    }

    /// <summary>
    /// Optimized helper function for Value Noise
    /// </summary>
    [BurstCompile]
    private static float RandomValue(int x, int y)
    {
        int hash = ((x * 12289) + y * 48611) % 1024;
        if (hash < 0) hash += 1024;

        float random = math.sin(x * 12.989f + y * 78.233f) * 43758.5453f;
        return random - math.floor(random);
    }

    /// <summary>
    /// 3D noise generation - useful for caves, mountains, etc.
    /// </summary>
    [BurstCompile]
    public static float Noise3D(float x, float y, float z, NoiseSettings settings)
    {
        float result = 0f;
        float amplitude = 1f;
        float frequency = 1f;

        for (int i = 0; i < settings.octaves; i++)
        {
            float nx = x * frequency * settings.scale;
            float ny = y * frequency * settings.scale;
            float nz = z * frequency * settings.scale;

            float noiseValue = 0f;

            switch (settings.noiseType)
            {
                case NoiseType.Perlin:
                    noiseValue = noise.cnoise(new float3(nx, ny, nz));
                    break;
                case NoiseType.Simplex:
                    noiseValue = noise.snoise(new float3(nx, ny, nz)) * 0.5f + 0.5f;
                    break;
            }

            result += noiseValue * amplitude;
            amplitude *= settings.persistence;
            frequency *= settings.lacunarity;
        }

        return result;
    }

    /// <summary>
    /// Multi-threaded noise generation job
    /// </summary>
    [BurstCompile]
    public struct NoiseGenerationJob : IJobParallelFor
    {
        public int width;
        public int height;
        public float scale;
        public int octaves;
        public float persistence;
        public float lacunarity;
        public float seed;
        public NoiseType noiseType;

        [WriteOnly]
        public NativeArray<float> results;

        public void Execute(int index)
        {
            int y = index / width;
            int x = index % width;

            float sampleX = x * scale + seed;
            float sampleY = y * scale + seed;

            float noiseValue = 0;
            float amplitude = 1;
            float frequency = 1;

            for (int i = 0; i < octaves; i++)
            {
                float nx = sampleX * frequency;
                float ny = sampleY * frequency;

                float value = 0;
                switch (noiseType)
                {
                    case NoiseType.Perlin:
                        value = noise.cnoise(new float2(nx, ny));
                        break;
                    case NoiseType.Simplex:
                        value = noise.snoise(new float2(nx, ny)) * 0.5f + 0.5f;
                        break;
                    case NoiseType.Ridged:
                        value = 1 - math.abs(noise.cnoise(new float2(nx, ny)));
                        value = value * value;
                        break;
                }

                noiseValue += value * amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            results[index] = noiseValue;
        }
    }

    /// <summary>
    /// Noise type enumeration
    /// </summary>
    public enum NoiseType
    {
        Perlin,
        Simplex,
        Value,
        Ridged,
        Cellular,
        FBM
    }

    /// <summary>
    /// Noise settings structure
    /// </summary>
    [System.Serializable]
    public struct NoiseSettings
    {
        public NoiseType noiseType;
        public float scale;
        public int octaves;
        public float persistence;
        public float lacunarity;
        public float offset;

        /// <summary>
        /// Constructor with default parameters
        /// </summary>
        public NoiseSettings(NoiseType type = NoiseType.Perlin, float scale = 0.1f, int octaves = 4,
                         float persistence = 0.5f, float lacunarity = 2.0f, float offset = 0f)
        {
            this.noiseType = type;
            this.scale = scale;
            this.octaves = octaves;
            this.persistence = persistence;
            this.lacunarity = lacunarity;
            this.offset = offset;
        }
    }
}