using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
using System.Linq;

/// <summary>
/// TerrainGenerator handles procedural generation of terrain by creating meshes,
/// applying noise functions, and managing terrain chunks.
/// </summary>
[ExecuteInEditMode]
public class TerrainGenerator : MonoBehaviour
{
    [SerializeField] public int xSize = 32;
    [SerializeField] public int zSize = 32;
    [SerializeField] public int chunkSize = 16;
    public float ChunkSize => chunkSize;
    [SerializeField] int xOffSet;
    [SerializeField] int zOffSet;
    [SerializeField] float noiseScale = 0.03f;
    [SerializeField] float heightMultipler = 7;
    [SerializeField] int octavesCount = 1;
    [SerializeField] float lacunarity = 2f;
    [SerializeField] float persistance = 0.5f;
    [SerializeField] float bottomDepth = -10f;
    [SerializeField] List<Layer> terrainLayers = new List<Layer>();
    [SerializeField] Material mat; // Material for texturing
    [SerializeField] float textureScale = 1f;

    public enum NoiseType { Perlin, Simplex, Value, Ridge, Cellular, FBM, DomainWarping }
    [SerializeField] NoiseType noiseType = NoiseType.Perlin;
    [SerializeField] AnimationCurve heightCurve = AnimationCurve.Linear(0, 0, 1, 1);
    [SerializeField] int seed = 0;

    [Header("LOD Settings")]
    [SerializeField] private bool enableLOD = true;
    [SerializeField] private float[] lodDistances = new float[] { 30f, 60f, 120f };
    [SerializeField] private bool enableFrustumCulling = true;

    [Header("Edge Falloff Settings")]
    [SerializeField] private bool useEdgeFalloff = true;
    [SerializeField, Range(1f, 10f)] private float falloffStrength = 3f;
    [SerializeField, Range(0.1f, 0.9f)] private float falloffStart = 0.8f;

    [Header("Performance")]
    [SerializeField] private bool useObjectPooling = true;
    [SerializeField] private int maxChunksInPool = 100;
    [SerializeField] private bool useBackgroundGeneration = true;
    [SerializeField] private float chunkGenerationInterval = 0.1f;
    [SerializeField] private bool useMultithreading = true;

    [Header("Seamless Terrain Settings")]
    [SerializeField] private bool enableSeamlessGeneration = true;
    [SerializeField] private int sharedVertexOverlap = 1; // Number of vertices to share for normal calculation
    [SerializeField] private bool normalizeNormals = true; // Whether to normalize calculated normals

    private List<GameObject> activeChunks = new List<GameObject>();
    private Queue<GameObject> chunkPool = new Queue<GameObject>();
    private float minTerrainHeight = float.MaxValue;
    private float maxTerrainHeight = float.MinValue;
    public List<GameObject> Chunks => activeChunks;
    public Material SharedMaterial => mat;

    private NativeArray<Vector3> vertices;
    private NativeArray<float> noiseValues;
    private NativeArray<JobHandle> chunkHandles;
    private JobHandle currentNoiseHandle;
    private bool isGenerating = false;
    private bool cancelRequested = false;
    private Texture2DArray cachedTextureArray;
    private float lastChunkUpdateTime;

    // Store shared normals for post-processing
    private Dictionary<Vector3, List<Vector3>> sharedNormals = new Dictionary<Vector3, List<Vector3>>();
    private Dictionary<Vector3, Vector3> finalNormals = new Dictionary<Vector3, Vector3>();

    // Queue for chunk generation
    private Queue<ChunkGenerationRequest> chunkGenerationQueue = new Queue<ChunkGenerationRequest>();
    private float lastUpdateTime;

    // Caching data to avoid memory allocations
    private Dictionary<int, int> chunkIndexCache = new Dictionary<int, int>();
    private Dictionary<System.Tuple<int, int>, GameObject> chunkPositionLookup = new Dictionary<System.Tuple<int, int>, GameObject>();

    // Edge vertex tracking - used to ensure shared vertices between chunks
    private Dictionary<long, Vector3> globalVertexPositions = new Dictionary<long, Vector3>();
    private Dictionary<long, Vector3> globalVertexNormals = new Dictionary<long, Vector3>();

    // Reusable arrays for better memory management
    private Vector3[] reusableVertexArray;
    private Vector2[] reusableUVArray;
    private int[] reusableTriangleArray;
    private Color[] reusableColorArray;

    // Events for communication with other components
    public delegate void TerrainGenerationEvent(bool isComplete);
    public event TerrainGenerationEvent OnTerrainGenerationChanged;

    // Constants for performance tuning
    private const int VERTICES_PER_CHUNK = 4096; // Typical estimate
    private const float FRUSTUM_UPDATE_INTERVAL = 0.25f; // 4 updates per second
    private float lastFrustumUpdateTime;
    private Plane[] cameraFrustumPlanes = new Plane[6];

    /// <summary>
    /// Helper method to create a unique key for a global vertex position
    /// </summary>
    private long GetVertexKey(int x, int z)
    {
        // Combine x and z into a single long key
        return ((long)x << 32) | (uint)z;
    }

    /// <summary>
    /// Structure for storing chunk data
    /// </summary>
    private struct ChunkData
    {
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector2> UVs;
        public NativeArray<Color> Colors;
        public NativeArray<int> Triangles;
        public int xStart;
        public int zStart;

        public void Dispose()
        {
            if (Vertices.IsCreated) Vertices.Dispose();
            if (UVs.IsCreated) UVs.Dispose();
            if (Colors.IsCreated) Colors.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
        }
    }

    /// <summary>
    /// Structure for chunk generation request
    /// </summary>
    private struct ChunkGenerationRequest
    {
        public int xStart;
        public int zStart;
        public int xSize;
        public int zSize;
        public ChunkData ChunkData;
    }

    private void Awake()
    {
        // Initialize object pool
        if (useObjectPooling && Application.isPlaying)
        {
            InitializeChunkPool();
        }

        // Preallocate reusable arrays
        InitializeReusableArrays();
    }

    /// <summary>
    /// Initializes reusable arrays for better memory management
    /// </summary>
    private void InitializeReusableArrays()
    {
        // Allocate reusable arrays with estimated max sizes
        int maxVertices = (chunkSize + 1) * (chunkSize + 1) * 3; // *3 for surface + walls + bottom
        int maxTriangles = chunkSize * chunkSize * 6 * 3; // *3 for all potential triangles

        reusableVertexArray = new Vector3[maxVertices];
        reusableUVArray = new Vector2[maxVertices];
        reusableTriangleArray = new int[maxTriangles];
        reusableColorArray = new Color[maxVertices];
    }

    private void Update()
    {
        if (Application.isPlaying)
        {
            float currentTime = Time.time;

            // Update chunks with interval to save CPU
            if (currentTime - lastChunkUpdateTime > chunkGenerationInterval)
            {
                lastChunkUpdateTime = currentTime;
                UpdateChunkVisibility();
                ProcessChunkGenerationQueue();
            }
        }
    }

    /// <summary>
    /// Initializes the chunk object pool
    /// </summary>
    private void InitializeChunkPool()
    {
        // Preemptively create chunk pool
        for (int i = 0; i < maxChunksInPool; i++)
        {
            GameObject chunkObj = CreateEmptyChunk();
            chunkObj.SetActive(false);
            chunkPool.Enqueue(chunkObj);
        }
    }

    /// <summary>
    /// Creates an empty chunk for pooling
    /// </summary>
    private GameObject CreateEmptyChunk()
    {
        GameObject chunkObj = new GameObject("PooledChunk");
        chunkObj.transform.parent = transform;
        chunkObj.layer = 0; // Default layer

        // Add required components
        chunkObj.AddComponent<MeshFilter>();
        chunkObj.AddComponent<MeshRenderer>();
        chunkObj.AddComponent<MeshCollider>();

        if (enableLOD)
        {
            TerrainLOD lodComponent = chunkObj.AddComponent<TerrainLOD>();
            if (Camera.main != null)
            {
                lodComponent.Initialize(Camera.main, lodDistances);
            }
        }

        return chunkObj;
    }

    /// <summary>
    /// Gets a chunk from the object pool
    /// </summary>
    private GameObject GetChunkFromPool()
    {
        GameObject chunkObj;

        if (chunkPool.Count > 0)
        {
            chunkObj = chunkPool.Dequeue();
        }
        else
        {
            chunkObj = new GameObject("PooledChunk");
            chunkObj.AddComponent<MeshFilter>();
            chunkObj.AddComponent<MeshRenderer>();
            chunkObj.AddComponent<MeshCollider>();

            if (enableLOD)
            {
                TerrainLOD lodComponent = chunkObj.AddComponent<TerrainLOD>();
                if (Camera.main != null)
                {
                    lodComponent.Initialize(Camera.main, lodDistances);
                }
            }
        }

        chunkObj.SetActive(true);
        return chunkObj;
    }

    /// <summary>
    /// Returns a chunk to the object pool
    /// </summary>
    private void ReturnChunkToPool(GameObject chunk)
    {
        if (chunkPool.Count < maxChunksInPool)
        {
            chunk.SetActive(false);
            chunkPool.Enqueue(chunk);
        }
        else
        {
            DestroyImmediate(chunk);
        }
    }

    /// <summary>
    /// Updates chunk visibility based on camera frustum
    /// </summary>
    private void UpdateChunkVisibility()
    {
        if (!enableFrustumCulling || Camera.main == null || activeChunks.Count == 0)
            return;

        float currentTime = Time.time;

        // Only update frustum planes occasionally for better performance
        if (currentTime - lastFrustumUpdateTime > FRUSTUM_UPDATE_INTERVAL)
        {
            // Get camera frustum planes
            GeometryUtility.CalculateFrustumPlanes(Camera.main, cameraFrustumPlanes);
            lastFrustumUpdateTime = currentTime;
        }

        // Camera position for distance checks
        Vector3 cameraPos = Camera.main.transform.position;

        foreach (GameObject chunk in activeChunks)
        {
            if (chunk == null) continue;

            MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
            if (renderer == null) continue;

            // Distance check first (faster than frustum test)
            float sqrDist = (chunk.transform.position - cameraPos).sqrMagnitude;
            bool isVisible = sqrDist < 10000f; // ~100 units view distance squared

            if (isVisible)
            {
                // Only do frustum test if within reasonable distance
                isVisible = GeometryUtility.TestPlanesAABB(cameraFrustumPlanes, renderer.bounds);
            }

            // Enable/disable renderer for optimization
            if (renderer.enabled != isVisible)
                renderer.enabled = isVisible;
        }
    }

    /// <summary>
    /// Processes the chunk generation queue
    /// </summary>
    private void ProcessChunkGenerationQueue()
    {
        if (chunkGenerationQueue.Count == 0 || !useBackgroundGeneration)
            return;

        // Process several chunks per frame
        int chunksToProcess = Mathf.Min(3, chunkGenerationQueue.Count);

        for (int i = 0; i < chunksToProcess; i++)
        {
            if (chunkGenerationQueue.Count == 0) break;

            ChunkGenerationRequest request = chunkGenerationQueue.Dequeue();
            CreateChunkGameObject(request.xStart, request.zStart, request.ChunkData);
            request.ChunkData.Dispose();
        }
    }

    /// <summary>
    /// Generates the terrain
    /// </summary>
    [ContextMenu("Generate Terrain")]
    public void GenerateTerrain()
    {
        if (isGenerating)
        {
            CancelGeneration();
        }

        isGenerating = true;
        cancelRequested = false;

        // Clear shared vertex dictionaries
        globalVertexPositions.Clear();
        globalVertexNormals.Clear();
        sharedNormals.Clear();
        finalNormals.Clear();

        // Inform any listeners that generation has started
        OnTerrainGenerationChanged?.Invoke(false);

        // Calculate how many chunks we need to generate
        int xChunks = Mathf.CeilToInt((float)xSize / chunkSize);
        int zChunks = Mathf.CeilToInt((float)zSize / chunkSize);

        // Clear chunk position lookup
        chunkPositionLookup.Clear();

        // If using pooling, deactivate existing chunks instead of deleting
        if (useObjectPooling && Application.isPlaying)
        {
            RecycleActiveChunks();
        }
        else
        {
            ClearChunks();
        }

        // Reset height values
        minTerrainHeight = float.MaxValue;
        maxTerrainHeight = float.MinValue;

        // Clear generation queue
        while (chunkGenerationQueue.Count > 0)
        {
            ChunkGenerationRequest request = chunkGenerationQueue.Dequeue();
            request.ChunkData.Dispose();
        }

        UnityEngine.Random.InitState(seed);
        float randomOffset = UnityEngine.Random.value * 1000f;

        // Calculate the total number of vertices for the entire terrain
        int totalVertices = (xSize + 1) * (zSize + 1);
        vertices = new NativeArray<Vector3>(totalVertices, Allocator.TempJob);
        noiseValues = new NativeArray<float>(totalVertices, Allocator.TempJob);

        // Job for noise generation
        var noiseJob = new NoiseGenerationJob
        {
            xSize = xSize,
            zSize = zSize,
            noiseScale = noiseScale,
            heightMultipler = heightMultipler,
            octavesCount = octavesCount,
            lacunarity = lacunarity,
            persistance = persistance,
            noiseType = noiseType,
            randomOffset = randomOffset,
            noiseValues = noiseValues,
            vertices = vertices,
            seed = seed,
            chunkSize = chunkSize,
            xOffSet = xOffSet,
            zOffSet = zOffSet,
            useEdgeFalloff = useEdgeFalloff,
            falloffStart = falloffStart,
            falloffStrength = falloffStrength,
            bottomDepth = bottomDepth
        };

        // Schedule and run Job
        currentNoiseHandle = noiseJob.Schedule(totalVertices, 64);

        // Need to wait for completion to apply AnimationCurve
        // Because AnimationCurve can't be used inside Job
        currentNoiseHandle.Complete();

        if (cancelRequested)
        {
            // If cancellation was requested, release resources and exit
            CleanupResources();
            isGenerating = false;
            OnTerrainGenerationChanged?.Invoke(true);
            return;
        }

        // Apply AnimationCurve
        ApplyHeightCurve();

        // Preprocess and cache global vertex positions for shared vertices
        if (enableSeamlessGeneration)
        {
            CacheGlobalVertexPositions();
        }

        // Generate chunks
        chunkHandles = new NativeArray<JobHandle>(xChunks * zChunks, Allocator.TempJob);
        List<ChunkData> chunkDataList = new List<ChunkData>();

        // First job has no dependencies, only depends on completion of noise job
        JobHandle previousHandle = currentNoiseHandle;

        for (int cz = 0; cz < zChunks; cz++)
        {
            for (int cx = 0; cx < xChunks; cx++)
            {
                // Calculate actual size for this chunk (might be smaller at edges)
                int chunkXSize = Mathf.Min(chunkSize, xSize - cx * chunkSize);
                int chunkZSize = Mathf.Min(chunkSize, zSize - cz * chunkSize);
                int chunkIndex = cz * xChunks + cx;

                ChunkData chunkData = PrepareChunkData(cx, cz, chunkXSize, chunkZSize, xChunks);

                var chunkJob = new ChunkGenerationJob
                {
                    xStart = cx * chunkSize,
                    zStart = cz * chunkSize,
                    chunkXSize = chunkXSize,
                    chunkZSize = chunkZSize,
                    xSize = xSize,
                    zSize = zSize,
                    bottomDepth = bottomDepth,
                    GlobalVertices = vertices,
                    ChunkVertices = chunkData.Vertices,
                    UVs = chunkData.UVs,
                    Colors = chunkData.Colors,
                    Triangles = chunkData.Triangles,
                    HasLeftWall = cx == 0,
                    HasRightWall = cx * chunkSize + chunkXSize >= xSize,
                    HasBottomWall = cz == 0,
                    HasTopWall = cz * chunkSize + chunkZSize >= zSize,
                    UseGlobalUVCalculation = enableSeamlessGeneration, // Use global UVs for seamless texturing
                    TextureScale = textureScale
                };

                // Schedule chunk generation job, specifying dependency on previous job
                JobHandle handle = chunkJob.Schedule(previousHandle);
                chunkHandles[chunkIndex] = handle;
                previousHandle = handle;

                ChunkGenerationRequest request = new ChunkGenerationRequest
                {
                    xStart = cx * chunkSize,
                    zStart = cz * chunkSize,
                    xSize = chunkXSize,
                    zSize = chunkZSize,
                    ChunkData = chunkData
                };

                chunkDataList.Add(chunkData);

                // If using background generation, add request to queue
                if (useBackgroundGeneration && Application.isPlaying)
                {
                    chunkGenerationQueue.Enqueue(request);
                }
            }
        }

        // Wait for all jobs to complete
        JobHandle.CompleteAll(chunkHandles);

        if (cancelRequested)
        {
            // If cancellation was requested, release resources and exit
            foreach (var chunkData in chunkDataList)
            {
                chunkData.Dispose();
            }
            CleanupResources();
            isGenerating = false;
            OnTerrainGenerationChanged?.Invoke(true);
            return;
        }

        // If not using background generation, create chunks immediately
        if (!useBackgroundGeneration || !Application.isPlaying)
        {
            for (int cz = 0; cz < zChunks; cz++)
            {
                for (int cx = 0; cx < xChunks; cx++)
                {
                    int index = cz * xChunks + cx;
                    if (index < chunkDataList.Count)
                    {
                        CreateChunkGameObject(cx * chunkSize, cz * chunkSize, chunkDataList[index]);
                        chunkDataList[index].Dispose();
                    }
                }
            }

            // For seamless normal calculation, do a second pass to fix normals
            if (enableSeamlessGeneration)
            {
                CalculateAndApplySharedNormals();
            }
        }

        // Generate textures
        GenerateTexture();

        CleanupResources();
        isGenerating = false;

        // Inform listeners that generation has completed
        OnTerrainGenerationChanged?.Invoke(true);
    }

    /// <summary>
    /// Cache the global vertex positions for later use in seamless generation
    /// </summary>
    private void CacheGlobalVertexPositions()
    {
        for (int z = 0; z <= zSize; z++)
        {
            for (int x = 0; x <= xSize; x++)
            {
                int index = z * (xSize + 1) + x;
                long key = GetVertexKey(x, z);

                // Store the vertex position in world space
                globalVertexPositions[key] = new Vector3(x, vertices[index].y, z);
            }
        }
    }

    /// <summary>
    /// Calculate and apply shared normals across chunk boundaries
    /// </summary>
    private void CalculateAndApplySharedNormals()
    {
        // First, collect normal contributions from all chunks
        foreach (GameObject chunk in activeChunks)
        {
            if (chunk == null) continue;

            MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) continue;

            Mesh mesh = meshFilter.sharedMesh;
            Vector3[] verts = mesh.vertices;
            Vector3[] normals = mesh.normals;

            // Get chunk world position
            Vector3 chunkPos = chunk.transform.position;

            // For each vertex
            for (int i = 0; i < verts.Length; i++)
            {
                // Convert to world space
                Vector3 worldVertex = chunkPos + verts[i];

                // Only process surface vertices (not wall or bottom vertices)
                if (IsVertexOnSurface(worldVertex, verts, i))
                {
                    // Round to avoid floating point issues
                    Vector3 key = new Vector3(
                        Mathf.Round(worldVertex.x * 1000f) / 1000f,
                        Mathf.Round(worldVertex.y * 1000f) / 1000f,
                        Mathf.Round(worldVertex.z * 1000f) / 1000f
                    );

                    // Add this vertex's normal to the collection
                    if (!sharedNormals.ContainsKey(key))
                    {
                        sharedNormals[key] = new List<Vector3>();
                    }
                    sharedNormals[key].Add(normals[i]);
                }
            }
        }

        // Next, average the normals for each shared vertex
        foreach (var kvp in sharedNormals)
        {
            Vector3 avgNormal = Vector3.zero;
            foreach (Vector3 normal in kvp.Value)
            {
                avgNormal += normal;
            }

            // Normalize the average
            if (avgNormal != Vector3.zero && normalizeNormals)
            {
                avgNormal.Normalize();
            }
            else if (avgNormal == Vector3.zero)
            {
                avgNormal = Vector3.up; // Fallback
            }

            finalNormals[kvp.Key] = avgNormal;
        }

        // Finally, apply the averaged normals back to all chunks
        foreach (GameObject chunk in activeChunks)
        {
            if (chunk == null) continue;

            MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) continue;

            Mesh mesh = meshFilter.sharedMesh;
            Vector3[] verts = mesh.vertices;
            Vector3[] normals = mesh.normals;

            // Get chunk world position
            Vector3 chunkPos = chunk.transform.position;

            bool normalsModified = false;

            // For each vertex
            for (int i = 0; i < verts.Length; i++)
            {
                // Convert to world space
                Vector3 worldVertex = chunkPos + verts[i];

                // Only process surface vertices
                if (IsVertexOnSurface(worldVertex, verts, i))
                {
                    // Round to avoid floating point issues
                    Vector3 key = new Vector3(
                        Mathf.Round(worldVertex.x * 1000f) / 1000f,
                        Mathf.Round(worldVertex.y * 1000f) / 1000f,
                        Mathf.Round(worldVertex.z * 1000f) / 1000f
                    );

                    // Apply the averaged normal if available
                    if (finalNormals.TryGetValue(key, out Vector3 avgNormal))
                    {
                        normals[i] = avgNormal;
                        normalsModified = true;
                    }
                }
            }

            // Update the mesh normals if any were modified
            if (normalsModified)
            {
                mesh.normals = normals;
            }
        }
    }

    /// <summary>
    /// Check if a vertex is on the surface (not part of walls or bottom)
    /// </summary>
    private bool IsVertexOnSurface(Vector3 worldVertex, Vector3[] allVertices, int vertexIndex)
    {
        // Simple heuristic: if the Y value is bottom_depth, it's not a surface vertex
        if (Mathf.Approximately(worldVertex.y, bottomDepth))
            return false;

        // Check if this vertex is one of the top vertices
        // (assuming the first (chunkSize+1)*(chunkSize+1) vertices are the top surface)
        return vertexIndex < (chunkSize + 1) * (chunkSize + 1);
    }

    /// <summary>
    /// Prepares chunk data for generation
    /// </summary>
    private ChunkData PrepareChunkData(int cx, int cz, int chunkXSize, int chunkZSize, int xChunks)
    {
        bool hasLeftWall = cx == 0;
        bool hasRightWall = cx * chunkSize + chunkXSize >= xSize;
        bool hasBottomWall = cz == 0;
        bool hasTopWall = cz * chunkSize + chunkZSize >= zSize;

        int surfaceVertCount = (chunkXSize + 1) * (chunkZSize + 1);
        int leftWallVertCount = hasLeftWall ? 2 * (chunkZSize + 1) : 0;
        int rightWallVertCount = hasRightWall ? 2 * (chunkZSize + 1) : 0;
        int bottomWallVertCount = hasBottomWall ? 2 * (chunkXSize + 1) : 0;
        int topWallVertCount = hasTopWall ? 2 * (chunkXSize + 1) : 0;
        int bottomVertCount = surfaceVertCount;

        int totalVertCount = surfaceVertCount + leftWallVertCount + rightWallVertCount +
                             bottomWallVertCount + topWallVertCount + bottomVertCount;

        int numTriangles = (chunkXSize * chunkZSize * 6) +
                           (hasLeftWall ? chunkZSize * 6 : 0) +
                           (hasRightWall ? chunkZSize * 6 : 0) +
                           (hasBottomWall ? chunkXSize * 6 : 0) +
                           (hasTopWall ? chunkXSize * 6 : 0) +
                           (chunkXSize * chunkZSize * 6);

        return new ChunkData
        {
            Vertices = new NativeArray<Vector3>(totalVertCount, Allocator.TempJob),
            UVs = new NativeArray<Vector2>(totalVertCount, Allocator.TempJob),
            Colors = new NativeArray<Color>(totalVertCount, Allocator.TempJob),
            Triangles = new NativeArray<int>(numTriangles, Allocator.TempJob),
            xStart = cx * chunkSize,
            zStart = cz * chunkSize
        };
    }

    /// <summary>
    /// Applies height curve to noise values
    /// </summary>
    private void ApplyHeightCurve()
    {
        // Apply AnimationCurve to heights
        for (int i = 0; i < vertices.Length; i++)
        {
            float yPos = noiseValues[i];
            yPos = heightCurve.Evaluate(yPos / heightMultipler) * heightMultipler;

            // Update vertex and noise value
            Vector3 vertex = vertices[i];
            vertices[i] = new Vector3(vertex.x, yPos, vertex.z);
            noiseValues[i] = yPos;
        }
    }

    /// <summary>
    /// Noise generation job definition
    /// </summary>
    [BurstCompile]
    struct NoiseGenerationJob : IJobParallelFor
    {
        public int xSize;
        public int zSize;
        public int chunkSize;
        public float noiseScale;
        public float heightMultipler;
        public int octavesCount;
        public float lacunarity;
        public float persistance;
        public NoiseType noiseType;
        public float randomOffset;
        public NativeArray<float> noiseValues;
        public NativeArray<Vector3> vertices;
        public int seed;
        public int xOffSet;
        public int zOffSet;
        public bool useEdgeFalloff;
        public float falloffStart;
        public float falloffStrength;
        public float bottomDepth;

        public void Execute(int index)
        {
            int x = index % (xSize + 1);
            int z = index / (xSize + 1);

            float yPos = 0;
            int globalX = x + seed + xOffSet;
            int globalZ = z + seed + zOffSet;

            // Optimization: compute octaves of noise
            for (int o = 0; o < octavesCount; o++)
            {
                float frequency = math.pow(lacunarity, o);
                float amplitude = math.pow(persistance, o);
                float sampleX = (globalX + randomOffset) * noiseScale * frequency;
                float sampleZ = (globalZ + randomOffset) * noiseScale * frequency;

                float noiseValue = CalculateNoise(sampleX, sampleZ);
                yPos += noiseValue * amplitude;
            }

            yPos *= heightMultipler;

            // Apply height falloff to edges if enabled
            if (useEdgeFalloff)
            {
                float falloffValue = CalculateFalloff(x, z);
                yPos = math.lerp(yPos, bottomDepth, falloffValue);
            }

            vertices[index] = new float3(x, yPos, z);
            noiseValues[index] = yPos;
        }

        private float CalculateNoise(float x, float z)
        {
            float noiseValue = 0;

            switch (noiseType)
            {
                case NoiseType.Perlin:
                    noiseValue = noise.cnoise(new float2(x, z));
                    break;
                case NoiseType.Simplex:
                    noiseValue = noise.snoise(new float2(x, z)) * 0.5f + 0.5f;
                    break;
                case NoiseType.Value:
                    noiseValue = NoiseFunctions.ValueNoise(x, z);
                    break;
                case NoiseType.Ridge:
                    noiseValue = NoiseFunctions.RidgeNoise(x, z);
                    break;
                case NoiseType.FBM:
                    // For FBM we compute only one octave here, as we already have octave loop
                    noiseValue = noise.cnoise(new float2(x, z));
                    break;
                case NoiseType.DomainWarping:
                    noiseValue = NoiseFunctions.DomainWarping(x, z);
                    break;
                case NoiseType.Cellular:
                    noiseValue = NoiseFunctions.CellularNoise(x, z, 10, 0, 0, chunkSize);
                    break;
            }

            return noiseValue;
        }

        private float CalculateFalloff(float x, float z)
        {
            if (!useEdgeFalloff)
                return 0f;

            // Normalize coordinates from 0 to 1
            float normalizedX = x / xSize;
            float normalizedZ = z / zSize;

            // Calculate value 0 in center and 1 at edges
            float distanceX = math.abs(normalizedX - 0.5f) * 2f;
            float distanceZ = math.abs(normalizedZ - 0.5f) * 2f;

            // Take maximum of two distances
            float value = math.max(distanceX, distanceZ);

            // Apply cutoff threshold
            value = math.max(0, (value - falloffStart) / (1 - falloffStart));

            // Apply power function for steepness adjustment
            return math.pow(value, falloffStrength);
        }
    }

    /// <summary>
    /// Chunk generation job definition
    /// </summary>
    [BurstCompile]
    struct ChunkGenerationJob : IJob
    {
        public int xStart;
        public int zStart;
        public int chunkXSize;
        public int chunkZSize;
        public int xSize;
        public int zSize;
        public float bottomDepth;
        public bool UseGlobalUVCalculation;
        public float TextureScale;
        public NativeArray<Vector3> GlobalVertices;
        public NativeArray<Vector3> ChunkVertices;
        public NativeArray<Vector2> UVs;
        public NativeArray<Color> Colors;
        public NativeArray<int> Triangles;
        public bool HasLeftWall;
        public bool HasRightWall;
        public bool HasBottomWall;
        public bool HasTopWall;

        public void Execute()
        {
            int surfaceVertCount = (chunkXSize + 1) * (chunkZSize + 1);
            int leftWallVertCount = HasLeftWall ? 2 * (chunkZSize + 1) : 0;
            int rightWallVertCount = HasRightWall ? 2 * (chunkZSize + 1) : 0;
            int bottomWallVertCount = HasBottomWall ? 2 * (chunkXSize + 1) : 0;
            int topWallVertCount = HasTopWall ? 2 * (chunkXSize + 1) : 0;
            int bottomVertCount = surfaceVertCount;

            // Generate top surface
            GenerateSurface(surfaceVertCount);

            int wallVertexIndex = surfaceVertCount;

            // Generate walls
            if (HasLeftWall)
                wallVertexIndex = GenerateWall(wallVertexIndex, true, false, Color.red);

            if (HasRightWall)
                wallVertexIndex = GenerateWall(wallVertexIndex, true, true, Color.green);

            if (HasBottomWall)
                wallVertexIndex = GenerateWall(wallVertexIndex, false, false, Color.blue);

            if (HasTopWall)
                wallVertexIndex = GenerateWall(wallVertexIndex, false, true, Color.yellow);

            // Generate bottom
            GenerateBottom(surfaceVertCount + leftWallVertCount + rightWallVertCount + bottomWallVertCount + topWallVertCount);

            // Generate triangles
            GenerateTriangles();
        }

        private void GenerateSurface(int surfaceVertCount)
        {
            for (int z = 0; z <= chunkZSize; z++)
            {
                for (int x = 0; x <= chunkXSize; x++)
                {
                    int vertexIndex = z * (chunkXSize + 1) + x;
                    int globalIndex = (z + zStart) * (xSize + 1) + (x + xStart);

                    if (globalIndex < GlobalVertices.Length)
                    {
                        Vector3 vertex = GlobalVertices[globalIndex];

                        // Keep global Y height but use local x, z coordinates for chunk positioning
                        ChunkVertices[vertexIndex] = new Vector3(x, vertex.y, z);

                        // Calculate UVs - either based on local or global coordinates
                        if (UseGlobalUVCalculation)
                        {
                            // Use global coordinates for seamless texture mapping
                            float globalX = x + xStart;
                            float globalZ = z + zStart;
                            UVs[vertexIndex] = new Vector2(globalX / TextureScale, globalZ / TextureScale);
                        }
                        else
                        {
                            // Old method: local UVs which may cause seams
                            UVs[vertexIndex] = new Vector2(x / (float)chunkXSize, z / (float)chunkZSize);
                        }

                        Colors[vertexIndex] = Color.white;
                    }
                }
            }
        }

        private int GenerateWall(int startIndex, bool isVertical, bool isPositive, Color wallColor)
        {
            int count = isVertical ? chunkZSize + 1 : chunkXSize + 1;

            for (int i = 0; i < count; i++)
            {
                int surfaceIndex;

                if (isVertical)
                {
                    // Vertical wall (left or right)
                    int x = isPositive ? chunkXSize : 0;
                    surfaceIndex = i * (chunkXSize + 1) + x;

                    float height = ChunkVertices[surfaceIndex].y;
                    ChunkVertices[startIndex] = new Vector3(x, height, i);

                    if (UseGlobalUVCalculation)
                    {
                        // Use global coordinates for UV calculation
                        float globalX = x + xStart;
                        float globalZ = i + zStart;
                        // For walls, use vertical UV mapping (height-based)
                        UVs[startIndex] = new Vector2(globalX / TextureScale, height / TextureScale);
                    }
                    else
                    {
                        // Original UV calculation
                        UVs[startIndex] = new Vector2(i / (float)(count - 1), height / 10f);
                    }
                }
                else
                {
                    // Horizontal wall (bottom or top)
                    int z = isPositive ? chunkZSize : 0;
                    surfaceIndex = z * (chunkXSize + 1) + i;

                    float height = ChunkVertices[surfaceIndex].y;
                    ChunkVertices[startIndex] = new Vector3(i, height, z);

                    if (UseGlobalUVCalculation)
                    {
                        // Use global coordinates for UV calculation
                        float globalX = i + xStart;
                        float globalZ = z + zStart;
                        // For walls, use vertical UV mapping (height-based)
                        UVs[startIndex] = new Vector2(globalX / TextureScale, height / TextureScale);
                    }
                    else
                    {
                        // Original UV calculation
                        UVs[startIndex] = new Vector2(i / (float)(count - 1), height / 10f);
                    }
                }

                Colors[startIndex] = wallColor;
                startIndex++;

                // Bottom vertex of wall
                if (isVertical)
                {
                    ChunkVertices[startIndex] = new Vector3(isPositive ? chunkXSize : 0, bottomDepth, i);
                }
                else
                {
                    ChunkVertices[startIndex] = new Vector3(i, bottomDepth, isPositive ? chunkZSize : 0);
                }

                if (UseGlobalUVCalculation)
                {
                    // Global UV for bottom vertices
                    float globalX = (isVertical ? (isPositive ? chunkXSize : 0) : i) + xStart;
                    float globalZ = (isVertical ? i : (isPositive ? chunkZSize : 0)) + zStart;
                    UVs[startIndex] = new Vector2(globalX / TextureScale, bottomDepth / TextureScale);
                }
                else
                {
                    UVs[startIndex] = new Vector2(i / (float)(count - 1), 0);
                }

                Colors[startIndex] = wallColor;
                startIndex++;
            }

            return startIndex;
        }

        private void GenerateBottom(int bottomStartIndex)
        {
            for (int z = 0; z <= chunkZSize; z++)
            {
                for (int x = 0; x <= chunkXSize; x++)
                {
                    int vertexIndex = bottomStartIndex + z * (chunkXSize + 1) + x;
                    ChunkVertices[vertexIndex] = new Vector3(x, bottomDepth, z);

                    if (UseGlobalUVCalculation)
                    {
                        // Use global coordinates for seamless bottom texturing
                        float globalX = x + xStart;
                        float globalZ = z + zStart;
                        UVs[vertexIndex] = new Vector2(globalX / TextureScale, globalZ / TextureScale);
                    }
                    else
                    {
                        // Original calculation
                        UVs[vertexIndex] = new Vector2(x / (float)chunkXSize * 2, z / (float)chunkZSize * 2);
                    }

                    Colors[vertexIndex] = new Color(0.5f, 0.5f, 0.5f); // Gray
                }
            }
        }

        private void GenerateTriangles()
        {
            int triangleIndex = 0;
            int surfaceVertCount = (chunkXSize + 1) * (chunkZSize + 1);

            // Top surface triangles
            for (int z = 0; z < chunkZSize; z++)
            {
                for (int x = 0; x < chunkXSize; x++)
                {
                    int vertexIndex = z * (chunkXSize + 1) + x;

                    // First triangle
                    Triangles[triangleIndex + 0] = vertexIndex;
                    Triangles[triangleIndex + 1] = vertexIndex + (chunkXSize + 1);
                    Triangles[triangleIndex + 2] = vertexIndex + 1;

                    // Second triangle
                    Triangles[triangleIndex + 3] = vertexIndex + 1;
                    Triangles[triangleIndex + 4] = vertexIndex + (chunkXSize + 1);
                    Triangles[triangleIndex + 5] = vertexIndex + (chunkXSize + 1) + 1;

                    triangleIndex += 6;
                }
            }

            // Wall and bottom triangles
            triangleIndex = GenerateWallTriangles(triangleIndex, surfaceVertCount, HasLeftWall, HasRightWall,
                                               HasBottomWall, HasTopWall);

            // Bottom triangles
            int bottomStartIndex = surfaceVertCount;
            if (HasLeftWall) bottomStartIndex += 2 * (chunkZSize + 1);
            if (HasRightWall) bottomStartIndex += 2 * (chunkZSize + 1);
            if (HasBottomWall) bottomStartIndex += 2 * (chunkXSize + 1);
            if (HasTopWall) bottomStartIndex += 2 * (chunkXSize + 1);

            for (int z = 0; z < chunkZSize; z++)
            {
                for (int x = 0; x < chunkXSize; x++)
                {
                    int vertexIndex = bottomStartIndex + z * (chunkXSize + 1) + x;

                    // Flipped triangles for bottom (facing down)
                    Triangles[triangleIndex + 0] = vertexIndex + 1;
                    Triangles[triangleIndex + 1] = vertexIndex + (chunkXSize + 1);
                    Triangles[triangleIndex + 2] = vertexIndex;
                    Triangles[triangleIndex + 3] = vertexIndex + (chunkXSize + 1) + 1;
                    Triangles[triangleIndex + 4] = vertexIndex + (chunkXSize + 1);
                    Triangles[triangleIndex + 5] = vertexIndex + 1;

                    triangleIndex += 6;
                }
            }
        }

        private int GenerateWallTriangles(int triangleIndex, int surfaceVertCount,
                                       bool hasLeftWall, bool hasRightWall,
                                       bool hasBottomWall, bool hasTopWall)
        {
            int vertOffset = 0;

            // Left wall
            if (hasLeftWall)
            {
                int leftWallStart = surfaceVertCount;
                for (int z = 0; z < chunkZSize; z++)
                {
                    int v0 = leftWallStart + z * 2;
                    int v1 = leftWallStart + (z + 1) * 2;
                    int v2 = leftWallStart + z * 2 + 1;
                    int v3 = leftWallStart + (z + 1) * 2 + 1;

                    Triangles[triangleIndex + 0] = v2;
                    Triangles[triangleIndex + 1] = v1;
                    Triangles[triangleIndex + 2] = v0;
                    Triangles[triangleIndex + 3] = v2;
                    Triangles[triangleIndex + 4] = v3;
                    Triangles[triangleIndex + 5] = v1;

                    triangleIndex += 6;
                }
                vertOffset += 2 * (chunkZSize + 1);
            }

            // Right wall
            if (hasRightWall)
            {
                int rightWallStart = surfaceVertCount + vertOffset;
                for (int z = 0; z < chunkZSize; z++)
                {
                    int v0 = rightWallStart + z * 2;
                    int v1 = rightWallStart + (z + 1) * 2;
                    int v2 = rightWallStart + z * 2 + 1;
                    int v3 = rightWallStart + (z + 1) * 2 + 1;

                    Triangles[triangleIndex + 0] = v0;
                    Triangles[triangleIndex + 1] = v1;
                    Triangles[triangleIndex + 2] = v2;
                    Triangles[triangleIndex + 3] = v2;
                    Triangles[triangleIndex + 4] = v1;
                    Triangles[triangleIndex + 5] = v3;

                    triangleIndex += 6;
                }
                vertOffset += 2 * (chunkZSize + 1);
            }

            // Bottom wall
            if (hasBottomWall)
            {
                int bottomWallStart = surfaceVertCount + vertOffset;
                for (int x = 0; x < chunkXSize; x++)
                {
                    int v0 = bottomWallStart + x * 2;
                    int v1 = bottomWallStart + (x + 1) * 2;
                    int v2 = bottomWallStart + x * 2 + 1;
                    int v3 = bottomWallStart + (x + 1) * 2 + 1;

                    Triangles[triangleIndex + 0] = v0;
                    Triangles[triangleIndex + 1] = v1;
                    Triangles[triangleIndex + 2] = v2;
                    Triangles[triangleIndex + 3] = v2;
                    Triangles[triangleIndex + 4] = v1;
                    Triangles[triangleIndex + 5] = v3;

                    triangleIndex += 6;
                }
                vertOffset += 2 * (chunkXSize + 1);
            }

            // Top wall
            if (hasTopWall)
            {
                int topWallStart = surfaceVertCount + vertOffset;
                for (int x = 0; x < chunkXSize; x++)
                {
                    int v0 = topWallStart + x * 2;
                    int v1 = topWallStart + (x + 1) * 2;
                    int v2 = topWallStart + x * 2 + 1;
                    int v3 = topWallStart + (x + 1) * 2 + 1;

                    Triangles[triangleIndex + 0] = v2;
                    Triangles[triangleIndex + 1] = v1;
                    Triangles[triangleIndex + 2] = v0;
                    Triangles[triangleIndex + 3] = v2;
                    Triangles[triangleIndex + 4] = v3;
                    Triangles[triangleIndex + 5] = v1;

                    triangleIndex += 6;
                }
            }

            return triangleIndex;
        }
    }

    /// <summary>
    /// Creates a game object for a chunk with the provided data
    /// </summary>
    private void CreateChunkGameObject(int xStart, int zStart, ChunkData chunkData)
    {
        // 1. Get or create chunk object
        GameObject chunkObj;

        if (useObjectPooling && Application.isPlaying)
        {
            chunkObj = GetChunkFromPool();
            chunkObj.name = $"Chunk_{xStart}_{zStart}";
            chunkObj.transform.localPosition = new Vector3(xStart, 0, zStart);
            chunkObj.SetActive(true);
        }
        else
        {
            chunkObj = new GameObject($"Chunk_{xStart}_{zStart}")
            {
                layer = LayerMask.NameToLayer("Chunks")
            };
            chunkObj.transform.SetParent(transform);
            chunkObj.transform.localPosition = new Vector3(xStart, 0, zStart);
        }

        // 2. Ensure required components
        MeshFilter meshFilter = chunkObj.GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            meshFilter = chunkObj.AddComponent<MeshFilter>();
        }

        MeshRenderer meshRenderer = chunkObj.GetComponent<MeshRenderer>();
        if (meshRenderer == null)
        {
            meshRenderer = chunkObj.AddComponent<MeshRenderer>();
        }

        MeshCollider meshCollider = chunkObj.GetComponent<MeshCollider>();
        if (meshCollider == null)
        {
            meshCollider = chunkObj.AddComponent<MeshCollider>();
        }

        // 3. Create new mesh (don't use sharedMesh which might be null)
        Mesh mesh = new Mesh
        {
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            name = $"ChunkMesh_{xStart}_{zStart}"
        };

        // 4. Prepare arrays for mesh data
        Vector3[] vertices = new Vector3[chunkData.Vertices.Length];
        Vector2[] uvs = new Vector2[chunkData.UVs.Length];
        Color[] colors = new Color[chunkData.Colors.Length];
        int[] triangles = new int[chunkData.Triangles.Length];

        // 5. Copy data from NativeArray
        chunkData.Vertices.CopyTo(vertices);
        chunkData.UVs.CopyTo(uvs);
        chunkData.Colors.CopyTo(colors);
        chunkData.Triangles.CopyTo(triangles);

        // 6. Assign data to mesh
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);

        // We'll calculate shared normals in a second pass if seamless generation is enabled
        if (!enableSeamlessGeneration)
        {
            mesh.RecalculateNormals();
        }
        else
        {
            // For seamless generation, still calculate normals as a starting point
            // but we'll fix them in a second pass
            mesh.RecalculateNormals();
        }

        mesh.RecalculateTangents();
        mesh.RecalculateBounds();

        // 7. Assign mesh to components
        meshFilter.sharedMesh = mesh;
        meshCollider.sharedMesh = enableLOD ? GenerateSimplifiedCollisionMesh(mesh) : mesh;

        // 8. Set up material
        if (mat != null)
        {
            meshRenderer.sharedMaterial = mat;
            MaterialPropertyBlock props = new MaterialPropertyBlock();
            props.SetVector("_ChunkPosition", chunkObj.transform.position);
            meshRenderer.SetPropertyBlock(props);
        }
        else
        {
            Debug.LogWarning("Material is not assigned to TerrainGenerator");
        }

        // 9. Add LOD component if needed
        if (enableLOD)
        {
            TerrainLOD lod = chunkObj.GetComponent<TerrainLOD>();
            if (lod == null)
            {
                lod = chunkObj.AddComponent<TerrainLOD>();
            }

            if (Camera.main != null)
            {
                lod.Initialize(Camera.main, lodDistances);
            }
        }

        // 10. Register chunk
        activeChunks.Add(chunkObj);
        var posKey = new System.Tuple<int, int>(xStart, zStart);
        chunkPositionLookup[posKey] = chunkObj;

        // 11. Update height range
        UpdateTerrainHeightRange(vertices, chunkObj.transform.position.y);
    }

    /// <summary>
    /// Generates a simplified collision mesh for better performance
    /// </summary>
    private Mesh GenerateSimplifiedCollisionMesh(Mesh sourceMesh)
    {
        // For collision we only need the top surface with reduced detail
        int simplificationFactor = 2; // Use every 2nd vertex

        // Get all vertices
        Vector3[] allVerts = sourceMesh.vertices;
        int[] allTriangles = sourceMesh.triangles;

        // Calculate surface size
        int surfaceVertCount = 0;
        float maxY = float.MinValue;

        // Find highest Y vertex which marks the surface
        for (int i = 0; i < allVerts.Length; i++)
        {
            if (allVerts[i].y > maxY)
            {
                maxY = allVerts[i].y;
            }
        }

        // Count vertices that are close to the top surface
        float threshold = 0.1f; // Tolerance for what counts as "surface"
        List<int> surfaceVertIndices = new List<int>();

        for (int i = 0; i < allVerts.Length; i++)
        {
            if (allVerts[i].y >= maxY - threshold)
            {
                surfaceVertIndices.Add(i);
                surfaceVertCount++;
            }
        }

        // Create simplified mesh
        Mesh collisionMesh = new Mesh();
        collisionMesh.name = "CollisionMesh";
        collisionMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        // Use original vertices instead of simplified ones
        collisionMesh.vertices = allVerts;

        // Find triangles that use only surface vertices
        List<int> simplifiedTriangles = new List<int>();

        for (int i = 0; i < allTriangles.Length; i += 3)
        {
            int v1 = allTriangles[i];
            int v2 = allTriangles[i + 1];
            int v3 = allTriangles[i + 2];

            // Check if any vertex is on surface
            if (allVerts[v1].y >= maxY - threshold ||
                allVerts[v2].y >= maxY - threshold ||
                allVerts[v3].y >= maxY - threshold)
            {
                simplifiedTriangles.Add(v1);
                simplifiedTriangles.Add(v2);
                simplifiedTriangles.Add(v3);
            }
        }

        collisionMesh.SetTriangles(simplifiedTriangles.ToArray(), 0);
        collisionMesh.RecalculateNormals();
        collisionMesh.RecalculateBounds();

        return collisionMesh;
    }

    /// <summary>
    /// Updates terrain height range for texture layering
    /// </summary>
    private void UpdateTerrainHeightRange(Vector3[] vertices, float yOffset)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            float worldY = vertices[i].y + yOffset;
            minTerrainHeight = Mathf.Min(minTerrainHeight, worldY);
            maxTerrainHeight = Mathf.Max(maxTerrainHeight, worldY);
        }
    }

    /// <summary>
    /// Generates textures for terrain
    /// </summary>
    private void GenerateTexture()
    {
        if (mat == null)
        {
            Debug.LogError("Material is null!");
            Shader terrainShader = Shader.Find("Custom/TerrainShaderURP");
            if (terrainShader == null)
            {
                Debug.LogError("Cannot find Custom/TerrainShaderURP shader! Using Fallback.");
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            }
            else
            {
                mat = new Material(terrainShader);
            }
        }

        // Check that we have at least one texture
        if (terrainLayers == null || terrainLayers.Count == 0)
        {
            Debug.LogWarning("Terrain layers is empty or null! Creating default layers.");
            terrainLayers = new List<Layer>
        {
            new Layer { texture = Texture2D.whiteTexture, startHeight = 0.0f },
            new Layer { texture = Texture2D.blackTexture, startHeight = 0.5f }
        };
        }

        // Guard against errors: if min and max are too close, set them forcefully
        if (maxTerrainHeight - minTerrainHeight < 0.01f)
        {
            Debug.LogWarning("Min and max terrain heights are too close! Setting default range.");
            maxTerrainHeight = minTerrainHeight + 10f;
        }

        Debug.Log($"Terrain heights: Min={minTerrainHeight}, Max={maxTerrainHeight}");

        // Set heights and other parameters in material
        mat.SetFloat("minTerrainHeight", minTerrainHeight);
        mat.SetFloat("maxTerrainHeight", maxTerrainHeight);
        mat.SetFloat("_textureScale", textureScale);

        // Set texture layer count
        int layersCount = terrainLayers.Count;
        Debug.Log($"Setting {layersCount} texture layers");
        mat.SetInt("numTextures", layersCount);

        // Prepare height array for shader of fixed MAX_TEXTURES size
        float[] heights = new float[TerrainShaderTextureManager.MAX_TEXTURES];
        for (int i = 0; i < layersCount && i < TerrainShaderTextureManager.MAX_TEXTURES; i++)
        {
            heights[i] = terrainLayers[i].startHeight;
            Debug.Log($"Layer {i}: Height = {heights[i]}, Texture = {(terrainLayers[i].texture ? terrainLayers[i].texture.name : "null")}");
        }
        mat.SetFloatArray("terrainHeights", heights);

        // Create texture array using the helper class
        try
        {
            // Очищаем предыдущий текстурный массив если он был
            if (cachedTextureArray != null)
            {
                Destroy(cachedTextureArray);
                cachedTextureArray = null;
            }

            // Создаём новый текстурный массив
            cachedTextureArray = TerrainShaderTextureManager.CreateTextureArray(terrainLayers);

            // Устанавливаем массив в материал
            if (cachedTextureArray != null)
            {
                mat.SetTexture("terrainTextures", cachedTextureArray);
                Debug.Log("Texture array successfully applied to material");
            }
            else
            {
                Debug.LogError("Failed to create texture array");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error creating texture array: {e.Message}");
        }

        // Apply material to all chunks
        foreach (var chunk in activeChunks)
        {
            if (chunk != null)
            {
                MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = mat;

                    // Set chunk position
                    MaterialPropertyBlock props = new MaterialPropertyBlock();
                    props.SetVector("_ChunkPosition", chunk.transform.position);
                    renderer.SetPropertyBlock(props);
                }
            }
        }

        // Настраиваем систему LOD для отключения дальних чанков
        ConfigureChunkLODSystem();
    }

    /// <summary>
    /// Настраивает систему LOD для управления видимостью чанков на расстоянии
    /// </summary>
    private void ConfigureChunkLODSystem()
    {
        // Расстояния для LOD системы (на каких дистанциях должны отключаться чанки)
        float[] distances = new float[] { 150f, 300f, 500f };

        foreach (var chunk in activeChunks)
        {
            if (chunk != null)
            {
                // Отключаем стандартную систему LOD для текстур
                TerrainLOD lodComponent = chunk.GetComponent<TerrainLOD>();

                if (lodComponent == null)
                {
                    // Если компонент отсутствует, создаем его
                    lodComponent = chunk.AddComponent<TerrainLOD>();
                }

                // Инициализируем с настройками для скрытия чанков, а не изменения их детализации
                if (Camera.main != null)
                {
                    lodComponent.Initialize(Camera.main, distances);
                    lodComponent.SetLODMode(TerrainLOD.LODMode.HideChunks);
                }
            }
        }
    }

    /// <summary>
    /// Gets chunk at the specified position
    /// </summary>
    public GameObject GetChunkAt(Vector3 position)
    {
        // Calculate chunk coordinates
        int chunkX = Mathf.FloorToInt(position.x / chunkSize) * chunkSize;
        int chunkZ = Mathf.FloorToInt(position.z / chunkSize) * chunkSize;

        // Look for chunk by coordinates in dictionary
        var posKey = new System.Tuple<int, int>(chunkX, chunkZ);

        if (chunkPositionLookup.TryGetValue(posKey, out GameObject chunk))
            return chunk;

        // If not found in dictionary, search by old algorithm
        foreach (GameObject activeChunk in activeChunks)
        {
            if (activeChunk != null && Vector3.Distance(activeChunk.transform.position, new Vector3(chunkX, 0, chunkZ)) < 0.1f)
                return activeChunk;
        }

        return null;
    }

    /// <summary>
    /// Recycles active chunks to object pool
    /// </summary>
    private void RecycleActiveChunks()
    {
        foreach (var chunk in activeChunks)
        {
            if (chunk != null)
                ReturnChunkToPool(chunk);
        }

        activeChunks.Clear();
    }

    /// <summary>
    /// Clears all chunks
    /// </summary>
    private void ClearChunks()
    {
        foreach (var chunk in activeChunks)
        {
            if (chunk != null) DestroyImmediate(chunk);
        }

        activeChunks.Clear();
        chunkPositionLookup.Clear();
    }

    /// <summary>
    /// Cleans up resources
    /// </summary>
    private void CleanupResources()
    {
        // Release NativeArrays
        if (vertices.IsCreated) vertices.Dispose();
        if (noiseValues.IsCreated) noiseValues.Dispose();
        if (chunkHandles.IsCreated) chunkHandles.Dispose();
    }

    /// <summary>
    /// Cancels terrain generation
    /// </summary>
    private void CancelGeneration()
    {
        if (isGenerating)
        {
            cancelRequested = true;

            if (currentNoiseHandle.IsCompleted)
                currentNoiseHandle.Complete();

            // Clean up resources and reset flags
            CleanupResources();
            isGenerating = false;
            cancelRequested = false;

            // Notify listeners
            OnTerrainGenerationChanged?.Invoke(true);
        }
    }

    private void OnDestroy()
    {
        // Clean up resources when object is destroyed
        if (isGenerating)
            CancelGeneration();

        // Release cached texture array
        if (cachedTextureArray != null)
            Destroy(cachedTextureArray);

        // Очищаем кеш текстур
        TerrainShaderTextureManager.ClearTextureCache();

        // Clean up reusable arrays
        reusableVertexArray = null;
        reusableUVArray = null;
        reusableTriangleArray = null;
        reusableColorArray = null;
    }

    /// <summary>
    /// Layer class for terrain texturing
    /// </summary>
    [System.Serializable]
    public class Layer
    {
        public Texture2D texture;
        [Range(0, 1)] public float startHeight;
    }
}