using UnityEngine;

/// <summary>
/// Improved LOD system for terrain that handles distance-based rendering without white color issues
/// </summary>
public class TerrainLOD : MonoBehaviour
{
    /// <summary>
    /// Режим работы LOD системы
    /// </summary>
    public enum LODMode
    {
        /// <summary>Стандартный режим с изменением детализации меша</summary>
        DetailReduction,

        /// <summary>Режим скрытия чанков на расстоянии</summary>
        HideChunks
    }

    private Camera mainCamera;
    private MeshRenderer meshRenderer;
    private Transform cameraTransform;
    private MaterialPropertyBlock propertyBlock;

    [Header("LOD Settings")]
    [SerializeField] private float[] lodDistances = new float[] { 150f, 300f, 500f };
    [SerializeField] private LODMode lodMode = LODMode.HideChunks;

    // Optimization fields
    private float lastDistanceCheck;
    private Vector3 lastCameraPosition;
    private float lastUpdateTime;
    private float updateInterval = 0.1f; // Check every 100ms for better responsiveness

    // Current LOD level
    private int currentLODLevel = 0;
    private float currentDistance = 0f;

    // Constants for performance tuning
    private const float POSITION_CHANGE_THRESHOLD = 3.0f; // Smaller threshold for more frequent checks
    private const float LOD_CHECK_INTERVAL = 0.2f; // More frequent checks

    /// <summary>
    /// Initializes the LOD component with the specified camera and distances
    /// </summary>
    public void Initialize(Camera camera, float[] distances = null)
    {
        mainCamera = camera;

        if (camera != null)
        {
            cameraTransform = camera.transform;
            lastCameraPosition = cameraTransform.position;
        }

        meshRenderer = GetComponent<MeshRenderer>();
        propertyBlock = new MaterialPropertyBlock();

        if (distances != null && distances.Length > 0)
        {
            lodDistances = distances;
        }

        lastUpdateTime = Time.time;

        // Initial LOD setup
        if (meshRenderer != null)
        {
            meshRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetFloat("_LODLevel", 0); // Start with highest detail
            meshRenderer.SetPropertyBlock(propertyBlock);
        }
    }

    /// <summary>
    /// Sets the LOD mode
    /// </summary>
    public void SetLODMode(LODMode mode)
    {
        lodMode = mode;
    }

    private void Update()
    {
        if (mainCamera == null || meshRenderer == null) return;

        // Only update at intervals for optimization
        float currentTime = Time.time;
        if (currentTime - lastUpdateTime < updateInterval)
            return;

        lastUpdateTime = currentTime;

        // Check if camera has moved significantly
        float distanceMoved = Vector3.Distance(cameraTransform.position, lastCameraPosition);
        if (distanceMoved < POSITION_CHANGE_THRESHOLD && currentLODLevel > 0) // Skip update if camera hasn't moved much and we're not at highest detail
            return;

        lastCameraPosition = cameraTransform.position;

        // Calculate distance to camera
        currentDistance = Vector3.Distance(transform.position, cameraTransform.position);

        // Determine visibility and LOD level based on distance
        DetermineLODLevel();

        // Apply LOD changes if needed
        ApplyLODSettings();
    }

    /// <summary>
    /// Determines the appropriate LOD level based on distance
    /// </summary>
    private void DetermineLODLevel()
    {
        // Default to highest detail (level 0)
        int newLODLevel = 0;

        // Find appropriate LOD level based on distance
        for (int i = 0; i < lodDistances.Length; i++)
        {
            if (currentDistance > lodDistances[i])
            {
                newLODLevel = i + 1;
            }
            else
            {
                break;
            }
        }

        // Only update if LOD level changed
        if (newLODLevel != currentLODLevel)
        {
            currentLODLevel = newLODLevel;
        }
    }

    /// <summary>
    /// Applies LOD settings based on the current mode and level
    /// </summary>
    private void ApplyLODSettings()
    {
        bool isVisible = true;

        // Determine visibility
        if (lodDistances.Length > 0 && currentLODLevel > lodDistances.Length)
        {
            // Beyond max distance, hide completely
            isVisible = false;
        }

        // Apply visibility change if needed
        if (meshRenderer.enabled != isVisible)
        {
            meshRenderer.enabled = isVisible;
        }

        // If visible, apply LOD level to shader
        if (isVisible)
        {
            meshRenderer.GetPropertyBlock(propertyBlock);

            // Set LOD level for shader (normalized 0-1 value)
            float normalizedLOD = currentLODLevel / (float)(lodDistances.Length + 1);
            propertyBlock.SetFloat("_LODLevel", normalizedLOD);

            // Set distance for shader
            propertyBlock.SetFloat("_DistanceFromCamera", currentDistance);

            meshRenderer.SetPropertyBlock(propertyBlock);
        }
    }

    /// <summary>
    /// Checks if the object is in the camera's frustum
    /// </summary>
    private bool IsVisibleByCamera()
    {
        if (mainCamera == null) return true;

        // Skip check occasionally for optimization
        float currentTime = Time.time;
        if (currentTime - lastDistanceCheck < LOD_CHECK_INTERVAL)
            return meshRenderer.enabled;

        lastDistanceCheck = currentTime;

        // Distance check
        float sqrDistance = (transform.position - cameraTransform.position).sqrMagnitude;
        if (sqrDistance > 250000f) // 500 units squared
            return false;

        // Frustum check
        Bounds bounds = meshRenderer.bounds;
        Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(mainCamera);
        return GeometryUtility.TestPlanesAABB(frustumPlanes, bounds);
    }
}