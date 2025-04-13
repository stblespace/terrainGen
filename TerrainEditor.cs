using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Terrain editing tool that allows users to modify terrain in the scene view
/// </summary>
[ExecuteInEditMode]
public class TerrainEditor : MonoBehaviour
{
    [SerializeField] float brushSize = 2f;
    [SerializeField] float brushStrength = 0.1f;
    [SerializeField] float brushFalloff = 0.5f; // Added falloff for smoother editing
    [SerializeField] int undoStackSize = 10; // Number of undo operations to support

    private TerrainGenerator terrainGenerator;
    private Camera sceneCamera;
    private RaycastHit hitInfo;
    private bool isEditing = false;

    // Cached geometry data for performance
    private Dictionary<int, MeshData> meshDataCache = new Dictionary<int, MeshData>();
    private Dictionary<int, Vector3[]> originalVerticesCache = new Dictionary<int, Vector3[]>();

    // Undo/Redo functionality
    private Stack<TerrainEditOperation> undoStack = new Stack<TerrainEditOperation>();
    private Stack<TerrainEditOperation> redoStack = new Stack<TerrainEditOperation>();

    // Last scene view position for optimization
    private Vector3 lastHitPosition;
    private float lastBrushSize;
    private bool brushPositionChanged = false;

    // Editor update timing
    private double lastUpdateTime;
    private const double UPDATE_INTERVAL = 0.05; // 50ms

    /// <summary>
    /// Cached mesh data for a terrain chunk
    /// </summary>
    private class MeshData
    {
        public Mesh mesh;
        public Vector3[] vertices;
        public Vector3[] originalVertices;
        public Vector3[] normals;
        public int[] triangles;
        public Vector2[] uvs;

        public MeshData(Mesh sourceMesh)
        {
            mesh = sourceMesh;
            vertices = sourceMesh.vertices;
            originalVertices = (Vector3[])vertices.Clone(); // Keep a copy of original vertices
            normals = sourceMesh.normals;
            triangles = sourceMesh.triangles;
            uvs = sourceMesh.uv;
        }
    }

    /// <summary>
    /// Structure to store terrain edit operations for undo/redo
    /// </summary>
    private class TerrainEditOperation
    {
        public int meshInstanceID;
        public int[] modifiedVertexIndices;
        public Vector3[] originalPositions;
        public Vector3[] newPositions;

        public TerrainEditOperation(int meshID, List<int> indices, List<Vector3> before, List<Vector3> after)
        {
            meshInstanceID = meshID;
            modifiedVertexIndices = indices.ToArray();
            originalPositions = before.ToArray();
            newPositions = after.ToArray();
        }
    }

    private void OnEnable()
    {
        terrainGenerator = GetComponent<TerrainGenerator>();

        // Ensure we get Scene view related events
        SceneView.duringSceneGui += OnSceneGUI;

        // Initialize brush visualization
        lastBrushSize = brushSize;
    }

    private void OnDisable()
    {
        // Clean up event subscriptions
        SceneView.duringSceneGui -= OnSceneGUI;

        // Clear caches to free memory
        meshDataCache.Clear();
        originalVerticesCache.Clear();
    }

    /// <summary>
    /// Handles Scene GUI events for terrain editing
    /// </summary>
    private void OnSceneGUI(SceneView sceneView)
    {
        // This method is called only in the editor
        Event e = Event.current;
        sceneCamera = sceneView.camera;

        // Handle brush size adjustment with keyboard shortcuts
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.LeftBracket) // [
            {
                brushSize = Mathf.Max(0.5f, brushSize - 0.5f);
                e.Use();
                sceneView.Repaint();
            }
            else if (e.keyCode == KeyCode.RightBracket) // ]
            {
                brushSize += 0.5f;
                e.Use();
                sceneView.Repaint();
            }
            else if (e.keyCode == KeyCode.Z && e.control) // Ctrl+Z for undo
            {
                PerformUndo();
                e.Use();
                sceneView.Repaint();
            }
            else if (e.keyCode == KeyCode.Y && e.control) // Ctrl+Y for redo
            {
                PerformRedo();
                e.Use();
                sceneView.Repaint();
            }
        }

        // Mouse interaction for terrain editing
        if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
        {
            if (e.button == 0 || e.button == 1) // LMB for raise, RMB for lower
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

                if (Physics.Raycast(ray, out hitInfo))
                {
                    GameObject hitObject = hitInfo.collider.gameObject;

                    // Check if we hit our terrain
                    if (terrainGenerator.Chunks.Contains(hitObject))
                    {
                        // Record initial state for Undo if starting a new edit
                        if (e.type == EventType.MouseDown)
                        {
                            isEditing = true;
                            StartNewEdit(hitObject);
                        }

                        if (isEditing)
                        {
                            // Only modify terrain at appropriate intervals for performance
                            double currentTime = EditorApplication.timeSinceStartup;
                            if (currentTime - lastUpdateTime >= UPDATE_INTERVAL || e.type == EventType.MouseDown)
                            {
                                ModifyTerrain(hitInfo.point, e.button == 0);
                                lastUpdateTime = currentTime;
                            }
                        }

                        e.Use(); // Prevent other handlers from processing
                    }
                }
            }
        }

        // End editing on mouse up
        if (e.type == EventType.MouseUp)
        {
            if (isEditing)
            {
                isEditing = false;
                FinishCurrentEdit();
            }
        }

        // Enable continuous updates during editing
        if (e.type == EventType.Layout)
        {
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(GetHashCode(), FocusType.Passive));
        }

        // Check if brush position has changed to avoid unnecessary redraws
        if (Physics.Raycast(HandleUtility.GUIPointToWorldRay(e.mousePosition), out hitInfo))
        {
            brushPositionChanged = (lastHitPosition != hitInfo.point || lastBrushSize != brushSize);

            if (brushPositionChanged)
            {
                lastHitPosition = hitInfo.point;
                lastBrushSize = brushSize;
                sceneView.Repaint();
            }
        }
    }

    /// <summary>
    /// Starts a new edit operation for undo/redo tracking
    /// </summary>
    private void StartNewEdit(GameObject chunk)
    {
        // Clear redo stack when starting a new edit
        redoStack.Clear();

        // Cache original mesh data if needed
        MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            int instanceID = meshFilter.sharedMesh.GetInstanceID();
            if (!originalVerticesCache.ContainsKey(instanceID))
            {
                Vector3[] originalVerts = meshFilter.sharedMesh.vertices;
                originalVerticesCache[instanceID] = (Vector3[])originalVerts.Clone();
            }
        }
    }

    /// <summary>
    /// Completes the current edit operation and adds it to the undo stack
    /// </summary>
    private void FinishCurrentEdit()
    {
        // Implementation depends on how modifications are tracked
        // Add to undo stack if needed

        // Limit undo stack size
        while (undoStack.Count >= undoStackSize)
        {
            undoStack.Pop();
        }
    }

    /// <summary>
    /// Performs undo operation
    /// </summary>
    private void PerformUndo()
    {
        if (undoStack.Count == 0) return;

        TerrainEditOperation operation = undoStack.Pop();
        redoStack.Push(operation);

        // Find the mesh by ID
        GameObject chunk = FindChunkWithMesh(operation.meshInstanceID);
        if (chunk == null) return;

        MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        Mesh mesh = meshFilter.sharedMesh;
        Vector3[] vertices = mesh.vertices;

        // Restore original vertex positions
        for (int i = 0; i < operation.modifiedVertexIndices.Length; i++)
        {
            int vertexIndex = operation.modifiedVertexIndices[i];
            vertices[vertexIndex] = operation.originalPositions[i];
        }

        // Apply changes
        mesh.vertices = vertices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Update collider
        UpdateMeshCollider(chunk);
    }

    /// <summary>
    /// Performs redo operation
    /// </summary>
    private void PerformRedo()
    {
        if (redoStack.Count == 0) return;

        TerrainEditOperation operation = redoStack.Pop();
        undoStack.Push(operation);

        // Find the mesh by ID
        GameObject chunk = FindChunkWithMesh(operation.meshInstanceID);
        if (chunk == null) return;

        MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        Mesh mesh = meshFilter.sharedMesh;
        Vector3[] vertices = mesh.vertices;

        // Apply new vertex positions
        for (int i = 0; i < operation.modifiedVertexIndices.Length; i++)
        {
            int vertexIndex = operation.modifiedVertexIndices[i];
            vertices[vertexIndex] = operation.newPositions[i];
        }

        // Apply changes
        mesh.vertices = vertices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Update collider
        UpdateMeshCollider(chunk);
    }

    /// <summary>
    /// Finds a chunk containing a mesh with the specified instance ID
    /// </summary>
    private GameObject FindChunkWithMesh(int meshInstanceID)
    {
        foreach (GameObject chunk in terrainGenerator.Chunks)
        {
            MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null &&
                meshFilter.sharedMesh.GetInstanceID() == meshInstanceID)
            {
                return chunk;
            }
        }
        return null;
    }

    /// <summary>
    /// Modifies the terrain at the specified position
    /// </summary>
    private void ModifyTerrain(Vector3 position, bool raise)
    {
        // Track changes for undo/redo
        Dictionary<int, List<int>> modifiedVerticesIndices = new Dictionary<int, List<int>>();
        Dictionary<int, List<Vector3>> originalPositions = new Dictionary<int, List<Vector3>>();
        Dictionary<int, List<Vector3>> newPositions = new Dictionary<int, List<Vector3>>();

        foreach (GameObject chunk in terrainGenerator.Chunks)
        {
            MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) continue;

            Mesh mesh = meshFilter.sharedMesh;
            int meshID = mesh.GetInstanceID();

            // Get or create cached mesh data
            MeshData meshData;
            if (!meshDataCache.TryGetValue(meshID, out meshData))
            {
                meshData = new MeshData(mesh);
                meshDataCache[meshID] = meshData;
            }

            Vector3[] vertices = meshData.vertices;
            bool meshModified = false;

            // Initialize tracking collections if needed
            if (!modifiedVerticesIndices.ContainsKey(meshID))
            {
                modifiedVerticesIndices[meshID] = new List<int>();
                originalPositions[meshID] = new List<Vector3>();
                newPositions[meshID] = new List<Vector3>();
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                // Convert local vertex to world position
                Vector3 worldVertex = chunk.transform.TransformPoint(vertices[i]);

                // Calculate distance from edit point to vertex
                float distance = Vector3.Distance(position, worldVertex);

                // If vertex is within brush radius
                if (distance < brushSize)
                {
                    // Calculate influence based on distance (falloff)
                    float influence = Mathf.Pow(1 - distance / brushSize, brushFalloff);
                    float heightDelta = brushStrength * influence;

                    // Track original position for undo
                    modifiedVerticesIndices[meshID].Add(i);
                    originalPositions[meshID].Add(vertices[i]);

                    // Raise or lower vertex
                    if (raise)
                        vertices[i].y += heightDelta;
                    else
                        vertices[i].y -= heightDelta;

                    // Track new position for undo
                    newPositions[meshID].Add(vertices[i]);

                    meshModified = true;
                }
            }

            if (meshModified)
            {
                // Apply changes
                mesh.vertices = vertices;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                // Update collider
                UpdateMeshCollider(chunk);
            }
        }

        // Create undo operation
        foreach (int meshID in modifiedVerticesIndices.Keys)
        {
            if (modifiedVerticesIndices[meshID].Count > 0)
            {
                TerrainEditOperation operation = new TerrainEditOperation(
                    meshID,
                    modifiedVerticesIndices[meshID],
                    originalPositions[meshID],
                    newPositions[meshID]
                );

                undoStack.Push(operation);
            }
        }
    }

    /// <summary>
    /// Updates mesh collider after mesh changes
    /// </summary>
    private void UpdateMeshCollider(GameObject chunk)
    {
        MeshCollider meshCollider = chunk.GetComponent<MeshCollider>();
        if (meshCollider != null)
        {
            // Use a cached reference to shared mesh
            MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
            if (meshFilter != null)
            {
                meshCollider.sharedMesh = null;
                meshCollider.sharedMesh = meshFilter.sharedMesh;
            }
        }
    }

    /// <summary>
    /// Draws gizmos for visualizing the brush in the scene
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        // Visualize brush in scene
        SceneView sceneView = SceneView.currentDrawingSceneView;
        if (sceneView == null) return;

        Event e = Event.current;
        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit))
        {
            // Draw brush outline
            Gizmos.color = new Color(1, 1, 0, 0.2f);
            Gizmos.DrawSphere(hit.point, brushSize);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(hit.point, brushSize);

            // Draw inner falloff area
            float innerRadius = brushSize * (1 - brushFalloff);
            Gizmos.color = new Color(1, 0.5f, 0, 0.3f);
            Gizmos.DrawSphere(hit.point, innerRadius);
        }
    }
}