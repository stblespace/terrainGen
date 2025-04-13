using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using TerrainGeneration;
using System.IO;
using UnityEditorInternal;

#if UNITY_EDITOR
/// <summary>
/// Custom editor window for creating and editing biomes
/// </summary>
public class BiomeEditorWindow : EditorWindow
{
    private const string BIOME_PRESETS_FOLDER = "Assets/BiomePresets";

    // Editor data
    private List<BiomeData> biomes = new List<BiomeData>();
    private int selectedBiomeIndex = -1;
    private BiomeData selectedBiome;
    private BiomeData defaultBiome;

    // Visual display settings
    private Vector2 scrollPositionMain;
    private Vector2 scrollPositionBiome;
    private Vector2 scrollPositionVegetation;
    private Vector2 scrollPositionLayers;
    private bool showBiomeParameters = true;
    private bool showTextureSettings = true;
    private bool showVegetationSettings = true;
    private bool showPreview = true;

    // Editor tabs
    private int selectedTab = 0;
    private readonly string[] tabNames = { "Biomes", "Textures", "Vegetation", "Preview" };

    // Dictionaries for preview and editing
    private Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
    private Dictionary<string, GameObject> vegetationCache = new Dictionary<string, GameObject>();
    private Dictionary<BiomeType, BiomeData> presetCache = new Dictionary<BiomeType, BiomeData>();

    // Texture array for preview
    private Texture2DArray textureArray;

    // 3D preview scene
    private PreviewRenderUtility previewRenderer;

    // ReorderableList for texture layers and vegetation
    private ReorderableList textureLayersList;
    private ReorderableList vegetationList;

    // Preview for current biome
    private Mesh previewMesh;
    private Material previewMaterial;
    private GameObject previewTerrainObject;
    private bool initialized = false;

    // Biome map
    private Texture2D biomeMapTexture;
    private bool showBiomeMap = false;
    private BiomeManager biomeManager;

    // Preview generation settings
    private float noiseScale = 0.03f;
    private int octaves = 4;
    private float persistence = 0.5f;
    private float lacunarity = 2.0f;
    private float heightMult = 10f;
    private int previewSize = 128;
    private bool autoUpdatePreview = true;
    private float seed = 0f;
    
    // Cached arrays for preview generation
    private Vector3[] previewVertices;
    private int[] previewTriangles;
    private Vector2[] previewUVs;
    private Vector3[] previewNormals;
    
    // Editor update control
    private double lastEditorUpdateTime;
    private const double EDITOR_UPDATE_INTERVAL = 0.1; // 100ms

    [MenuItem("Tools/Biome Editor")]
    public static void ShowWindow()
    {
        BiomeEditorWindow window = GetWindow<BiomeEditorWindow>("Biome Editor");
        window.minSize = new Vector2(800, 600);
        window.Show();
    }

    private void OnEnable()
    {
        // Initialize when window opens
        Initialize();
        
        // Force initialize the arrays with defaults to avoid null references
        InitializePreviewArrays();
        
        // Register for editor updates
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        // Clean up resources
        if (previewRenderer != null)
        {
            previewRenderer.Cleanup();
            previewRenderer = null;
        }

        if (previewMesh != null)
        {
            DestroyImmediate(previewMesh);
            previewMesh = null;
        }

        if (previewMaterial != null)
        {
            DestroyImmediate(previewMaterial);
            previewMaterial = null;
        }

        if (previewTerrainObject != null)
        {
            DestroyImmediate(previewTerrainObject);
            previewTerrainObject = null;
        }

        if (textureArray != null)
        {
            DestroyImmediate(textureArray);
            textureArray = null;
        }
        
        // Unregister from editor updates
        EditorApplication.update -= OnEditorUpdate;
    }
    
    /// <summary>
    /// Initialize preview arrays with default sizes
    /// </summary>
    private void InitializePreviewArrays()
    {
        // Use default size for initial allocation
        int vertexCount = (previewSize + 1) * (previewSize + 1);
        int triangleCount = previewSize * previewSize * 6;
        
        previewVertices = new Vector3[vertexCount];
        previewTriangles = new int[triangleCount];
        previewUVs = new Vector2[vertexCount];
        previewNormals = new Vector3[vertexCount];
    }
    
    /// <summary>
    /// Editor update callback for handling UI updates efficiently
    /// </summary>
    private void OnEditorUpdate()
    {
        // Only update at fixed intervals to reduce CPU usage
        if (EditorApplication.timeSinceStartup - lastEditorUpdateTime < EDITOR_UPDATE_INTERVAL)
            return;
            
        lastEditorUpdateTime = EditorApplication.timeSinceStartup;
        
        // Handle preview update if window is focused and has auto-update enabled
        if (autoUpdatePreview && focusedWindow == this && previewMesh != null)
        {
            Repaint();
        }
    }

    private void Initialize()
    {
        if (initialized) return;

        // Load or create biomes
        LoadBiomes();

        // Create preview renderer
        CreatePreviewRenderer();

        // Create lists for editing
        CreateReorderableLists();

        // Find biome manager in scene
        biomeManager = UnityEngine.Object.FindFirstObjectByType<BiomeManager>();

        initialized = true;
    }

    private void LoadBiomes()
    {
        biomes.Clear();

        // First check if there's a biome manager in the scene
        var biomeManager = UnityEngine.Object.FindFirstObjectByType<BiomeManager>();
        if (biomeManager != null)
        {
            // Get biomes from biome manager
            var serializedManager = new SerializedObject(biomeManager);
            var biomesProp = serializedManager.FindProperty("biomes");

            if (biomesProp != null && biomesProp.isArray)
            {
                for (int i = 0; i < biomesProp.arraySize; i++)
                {
                    var biomeProp = biomesProp.GetArrayElementAtIndex(i);
                    var biomeObj = biomeProp.objectReferenceValue as BiomeData;

                    if (biomeObj != null)
                    {
                        biomes.Add(biomeObj);
                    }
                }
            }

            // Get default biome
            var defaultBiomeProp = serializedManager.FindProperty("defaultBiome");
            if (defaultBiomeProp != null)
            {
                defaultBiome = defaultBiomeProp.objectReferenceValue as BiomeData;
            }
        }

        // If no biomes found, create predefined ones
        if (biomes.Count == 0)
        {
            var plainsBiome = BiomeData.GetDefaultBiome(BiomeType.Plains);
            var desertBiome = BiomeData.GetDefaultBiome(BiomeType.Desert);
            var mountainsBiome = BiomeData.GetDefaultBiome(BiomeType.Mountains);

            biomes.Add(plainsBiome);
            biomes.Add(desertBiome);
            biomes.Add(mountainsBiome);

            defaultBiome = plainsBiome;
        }

        // Create preset folder if it doesn't exist
        if (!AssetDatabase.IsValidFolder(BIOME_PRESETS_FOLDER))
        {
            string parentFolder = Path.GetDirectoryName(BIOME_PRESETS_FOLDER);
            string folderName = Path.GetFileName(BIOME_PRESETS_FOLDER);
            AssetDatabase.CreateFolder(parentFolder, folderName);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // Set first biome as selected
        if (biomes.Count > 0)
        {
            selectedBiomeIndex = 0;
            selectedBiome = biomes[0];
        }
    }

    private void CreatePreviewRenderer()
    {
        previewRenderer = new PreviewRenderUtility();
        previewRenderer.camera.transform.position = new Vector3(0, 20, -20);
        previewRenderer.camera.transform.rotation = Quaternion.Euler(45, 0, 0);
        previewRenderer.camera.nearClipPlane = 0.1f;
        previewRenderer.camera.farClipPlane = 100f;

        // Add light
        previewRenderer.lights[0].type = LightType.Directional;
        previewRenderer.lights[0].intensity = 1.5f;
        previewRenderer.lights[0].transform.rotation = Quaternion.Euler(50, 30, 0);
        previewRenderer.lights[0].shadows = LightShadows.Soft;

        // Create material for preview
        previewMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        // Generate mesh for preview
        GeneratePreviewMesh();
    }

    private void CreateReorderableLists()
    {
        // Create list for texture layers
        textureLayersList = new ReorderableList(
            new List<TerrainTextureLayer>(),
            typeof(TerrainTextureLayer),
            true, true, true, true);

        textureLayersList.drawHeaderCallback = (Rect rect) => {
            EditorGUI.LabelField(rect, "Texture Layers");
        };

        textureLayersList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {
            if (selectedBiome == null || index >= selectedBiome.textureLayers.Count) return;

            var layer = selectedBiome.textureLayers[index];
            rect.y += 2;
            rect.height = EditorGUIUtility.singleLineHeight;

            // Display texture name
            EditorGUI.LabelField(
                new Rect(rect.x, rect.y, rect.width * 0.4f, rect.height),
                layer.textureName);

            // Display height range
            EditorGUI.MinMaxSlider(
                new Rect(rect.x + rect.width * 0.4f, rect.y, rect.width * 0.6f - 20, rect.height),
                ref layer.heightStart, ref layer.heightEnd, 0f, 1f);

            // Display texture preview
            if (layer.texture != null)
            {
                float previewSize = rect.height;
                EditorGUI.DrawPreviewTexture(
                    new Rect(rect.x + rect.width - previewSize, rect.y, previewSize, previewSize),
                    layer.texture);
            }
        };

        textureLayersList.onAddCallback = (ReorderableList list) => {
            if (selectedBiome == null) return;

            selectedBiome.textureLayers.Add(new TerrainTextureLayer
            {
                textureName = "NewTexture",
                heightStart = 0f,
                heightEnd = 1f,
                slopeStart = 0f,
                slopeEnd = 1f,
                tiling = 1f
            });

            if (autoUpdatePreview) RegeneratePreview();
        };

        textureLayersList.onRemoveCallback = (ReorderableList list) => {
            if (selectedBiome == null || list.index >= selectedBiome.textureLayers.Count) return;

            selectedBiome.textureLayers.RemoveAt(list.index);

            if (autoUpdatePreview) RegeneratePreview();
        };

        textureLayersList.onSelectCallback = (ReorderableList list) => {
            // Used to respond to element selection in the list
        };

        // Create list for vegetation
        vegetationList = new ReorderableList(
            new List<VegetationData>(),
            typeof(VegetationData),
            true, true, true, true);

        vegetationList.drawHeaderCallback = (Rect rect) => {
            EditorGUI.LabelField(rect, "Vegetation");
        };

        vegetationList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {
            if (selectedBiome == null || index >= selectedBiome.vegetation.Count) return;

            var veg = selectedBiome.vegetation[index];
            rect.y += 2;
            rect.height = EditorGUIUtility.singleLineHeight;

            // Display prefab name
            EditorGUI.LabelField(
                new Rect(rect.x, rect.y, rect.width * 0.4f, rect.height),
                veg.prefabName);

            // Display density
            veg.density = EditorGUI.Slider(
                new Rect(rect.x + rect.width * 0.4f, rect.y, rect.width * 0.5f, rect.height),
                veg.density, 0f, 1f);

            // Display height range
            EditorGUI.LabelField(
                new Rect(rect.x, rect.y + rect.height, rect.width * 0.2f, rect.height),
                "Height:");

            EditorGUI.MinMaxSlider(
                new Rect(rect.x + rect.width * 0.2f, rect.y + rect.height, rect.width * 0.7f, rect.height),
                ref veg.minHeight, ref veg.maxHeight, 0f, 1f);
        };

        vegetationList.onAddCallback = (ReorderableList list) => {
            if (selectedBiome == null) return;

            selectedBiome.vegetation.Add(new VegetationData
            {
                prefabName = "NewVegetation",
                minHeight = 0f,
                maxHeight = 1f,
                minSlope = 0f,
                maxSlope = 0.5f,
                density = 0.5f,
                randomRotation = true
            });
        };

        vegetationList.onRemoveCallback = (ReorderableList list) => {
            if (selectedBiome == null || list.index >= selectedBiome.vegetation.Count) return;

            selectedBiome.vegetation.RemoveAt(list.index);
        };
    }

    private void OnGUI()
    {
        if (!initialized) Initialize();

        EditorGUILayout.BeginHorizontal();

        // Left panel with biome list
        DrawBiomesList();

        // Right panel with editing
        DrawBiomeEditor();

        EditorGUILayout.EndHorizontal();
    }

    private void DrawBiomesList()
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(200));

        EditorGUILayout.LabelField("Biomes", EditorStyles.boldLabel);

        scrollPositionMain = EditorGUILayout.BeginScrollView(scrollPositionMain, GUILayout.Width(200), GUILayout.ExpandHeight(true));

        // Display biome list
        for (int i = 0; i < biomes.Count; i++)
        {
            BiomeData biome = biomes[i];

            EditorGUILayout.BeginHorizontal();

            // Field for biome selection with color indicator
            Rect colorRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16));
            EditorGUI.DrawRect(colorRect, biome.editorColor);

            // Biome selection button
            bool isSelected = GUILayout.Toggle(selectedBiomeIndex == i, biome.name, "Button");
            if (isSelected && selectedBiomeIndex != i)
            {
                selectedBiomeIndex = i;
                selectedBiome = biome;

                // Update lists
                if (selectedBiome != null)
                {
                    textureLayersList.list = selectedBiome.textureLayers;
                    vegetationList.list = selectedBiome.vegetation;
                }

                if (autoUpdatePreview) RegeneratePreview();
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();

        // Buttons for working with biomes
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Add"))
        {
            // Create new biome using ScriptableObject.CreateInstance instead of 'new BiomeData'
            BiomeData newBiome = BiomeData.CreateInstance("New Biome");
            newBiome.editorColor = new Color(
                UnityEngine.Random.value,
                UnityEngine.Random.value,
                UnityEngine.Random.value
            );

            biomes.Add(newBiome);
            selectedBiomeIndex = biomes.Count - 1;
            selectedBiome = newBiome;

            if (selectedBiome != null)
            {
                textureLayersList.list = selectedBiome.textureLayers;
                vegetationList.list = selectedBiome.vegetation;
            }

            if (autoUpdatePreview) RegeneratePreview();
        }

        GUI.enabled = selectedBiomeIndex >= 0;

        if (GUILayout.Button("Delete"))
        {
            if (selectedBiomeIndex >= 0 && selectedBiomeIndex < biomes.Count)
            {
                // Delete selected biome
                biomes.RemoveAt(selectedBiomeIndex);

                if (biomes.Count > 0)
                {
                    selectedBiomeIndex = Mathf.Clamp(selectedBiomeIndex, 0, biomes.Count - 1);
                    selectedBiome = biomes[selectedBiomeIndex];

                    if (selectedBiome != null)
                    {
                        textureLayersList.list = selectedBiome.textureLayers;
                        vegetationList.list = selectedBiome.vegetation;
                    }
                }
                else
                {
                    selectedBiomeIndex = -1;
                    selectedBiome = null;
                }

                if (autoUpdatePreview) RegeneratePreview();
            }
        }

        GUI.enabled = true;

        EditorGUILayout.EndHorizontal();

        // Load and save buttons
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Load Presets"))
        {
            LoadBiomePresets();
        }

        if (GUILayout.Button("Save Presets"))
        {
            SaveBiomePresets();
        }

        EditorGUILayout.EndHorizontal();

        // Apply to scene button
        if (biomeManager != null)
        {
            if (GUILayout.Button("Apply to Scene"))
            {
                ApplyToScene();
            }
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawBiomeEditor()
    {
        EditorGUILayout.BeginVertical();

        if (selectedBiome != null)
        {
            selectedTab = GUILayout.Toolbar(selectedTab, tabNames);

            switch (selectedTab)
            {
                case 0: // Biomes tab
                    DrawBiomeTab();
                    break;
                case 1: // Textures tab
                    DrawTexturesTab();
                    break;
                case 2: // Vegetation tab
                    DrawVegetationTab();
                    break;
                case 3: // Preview tab
                    DrawPreviewTab();
                    break;
            }
        }
        else
        {
            EditorGUILayout.HelpBox("Select a biome to edit", MessageType.Info);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawBiomeTab()
    {
        scrollPositionBiome = EditorGUILayout.BeginScrollView(scrollPositionBiome);

        EditorGUI.BeginChangeCheck();

        // Basic biome parameters
        EditorGUILayout.LabelField("Basic Parameters", EditorStyles.boldLabel);

        selectedBiome.name = EditorGUILayout.TextField("Name", selectedBiome.name);
        selectedBiome.type = (BiomeType)EditorGUILayout.EnumPopup("Biome Type", selectedBiome.type);
        selectedBiome.editorColor = EditorGUILayout.ColorField("Editor Color", selectedBiome.editorColor);

        EditorGUILayout.Space(10);

        // Climate parameters
        EditorGUILayout.LabelField("Climate Parameters", EditorStyles.boldLabel);

        selectedBiome.temperature = EditorGUILayout.Slider("Temperature", selectedBiome.temperature, 0f, 1f);
        selectedBiome.humidity = EditorGUILayout.Slider("Humidity", selectedBiome.humidity, 0f, 1f);
        selectedBiome.baseHeight = EditorGUILayout.Slider("Base Height", selectedBiome.baseHeight, 0f, 1f);
        selectedBiome.snowHeight = EditorGUILayout.Slider("Snow Height", selectedBiome.snowHeight, 0f, 1f);

        EditorGUILayout.Space(10);

        // Noise parameters
        EditorGUILayout.LabelField("Noise Parameters", EditorStyles.boldLabel);

        selectedBiome.noiseSettings.noiseType = (NoiseFunctions.NoiseType)EditorGUILayout.EnumPopup(
            "Noise Type", selectedBiome.noiseSettings.noiseType);

        selectedBiome.noiseSettings.scale = EditorGUILayout.FloatField("Noise Scale", selectedBiome.noiseSettings.scale);
        selectedBiome.noiseSettings.octaves = EditorGUILayout.IntSlider("Octaves", selectedBiome.noiseSettings.octaves, 1, 8);
        selectedBiome.noiseSettings.persistence = EditorGUILayout.Slider("Persistence", selectedBiome.noiseSettings.persistence, 0.1f, 1f);
        selectedBiome.noiseSettings.lacunarity = EditorGUILayout.Slider("Lacunarity", selectedBiome.noiseSettings.lacunarity, 1f, 4f);
        selectedBiome.heightMultiplier = EditorGUILayout.FloatField("Height Multiplier", selectedBiome.heightMultiplier);
        selectedBiome.slopeMultiplier = EditorGUILayout.Slider("Slope Steepness", selectedBiome.slopeMultiplier, 0f, 2f);

        EditorGUILayout.Space(5);

        // Height curve
        EditorGUILayout.LabelField("Height Curve");
        selectedBiome.heightCurve = EditorGUILayout.CurveField(selectedBiome.heightCurve);

        EditorGUILayout.Space(10);

        // General texturing parameters
        EditorGUILayout.LabelField("General Texturing Parameters", EditorStyles.boldLabel);

        selectedBiome.textureScale = EditorGUILayout.Slider("Texture Scale", selectedBiome.textureScale, 0.1f, 10f);
        selectedBiome.blendStrength = EditorGUILayout.Slider("Blend Strength", selectedBiome.blendStrength, 0f, 1f);

        EditorGUILayout.Space(10);

        // Vegetation parameters
        EditorGUILayout.LabelField("General Vegetation Parameters", EditorStyles.boldLabel);

        selectedBiome.vegetationDensity = EditorGUILayout.Slider("Vegetation Density", selectedBiome.vegetationDensity, 0f, 1f);

        if (EditorGUI.EndChangeCheck() && autoUpdatePreview)
        {
            RegeneratePreview();
        }

        EditorGUILayout.Space(20);

        // Preset biome loading buttons
        EditorGUILayout.LabelField("Load Preset", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Plains"))
        {
            LoadPresetBiome(BiomeType.Plains);
        }

        if (GUILayout.Button("Desert"))
        {
            LoadPresetBiome(BiomeType.Desert);
        }

        if (GUILayout.Button("Mountains"))
        {
            LoadPresetBiome(BiomeType.Mountains);
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Forest"))
        {
            LoadPresetBiome(BiomeType.Forest);
        }

        if (GUILayout.Button("Swamp"))
        {
            LoadPresetBiome(BiomeType.Swamp);
        }

        if (GUILayout.Button("Tundra"))
        {
            LoadPresetBiome(BiomeType.Tundra);
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Taiga"))
        {
            LoadPresetBiome(BiomeType.Taiga);
        }

        if (GUILayout.Button("Savanna"))
        {
            LoadPresetBiome(BiomeType.Savanna);
        }

        if (GUILayout.Button("Jungle"))
        {
            LoadPresetBiome(BiomeType.Jungle);
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndScrollView();
    }

    private void DrawTexturesTab()
    {
        scrollPositionLayers = EditorGUILayout.BeginScrollView(scrollPositionLayers);

        EditorGUILayout.LabelField("Biome Texture Layers", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Configure texture layers for different heights and slopes", MessageType.Info);

        EditorGUI.BeginChangeCheck();

        // Display texture layer list
        textureLayersList.DoLayoutList();

        // If layer is selected, show detailed settings
        int selectedLayerIndex = textureLayersList.index;
        if (selectedLayerIndex >= 0 && selectedLayerIndex < selectedBiome.textureLayers.Count)
        {
            TerrainTextureLayer layer = selectedBiome.textureLayers[selectedLayerIndex];

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Layer Settings", EditorStyles.boldLabel);

            // Basic layer parameters
            layer.textureName = EditorGUILayout.TextField("Texture Name", layer.textureName);

            // Height settings
            EditorGUILayout.LabelField("Height Range");
            EditorGUILayout.BeginHorizontal();
            layer.heightStart = EditorGUILayout.FloatField(layer.heightStart, GUILayout.Width(50));
            EditorGUILayout.MinMaxSlider(ref layer.heightStart, ref layer.heightEnd, 0f, 1f);
            layer.heightEnd = EditorGUILayout.FloatField(layer.heightEnd, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            // Slope settings
            EditorGUILayout.LabelField("Slope Range");
            EditorGUILayout.BeginHorizontal();
            layer.slopeStart = EditorGUILayout.FloatField(layer.slopeStart, GUILayout.Width(50));
            EditorGUILayout.MinMaxSlider(ref layer.slopeStart, ref layer.slopeEnd, 0f, 1f);
            layer.slopeEnd = EditorGUILayout.FloatField(layer.slopeEnd, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            // Texture and normal map
            layer.texture = (Texture2D)EditorGUILayout.ObjectField("Texture", layer.texture, typeof(Texture2D), false);
            layer.normalMap = (Texture2D)EditorGUILayout.ObjectField("Normal Map", layer.normalMap, typeof(Texture2D), false);
            layer.tiling = EditorGUILayout.Slider("Tiling", layer.tiling, 0.1f, 10f);
            layer.normalStrength = EditorGUILayout.Slider("Normal Strength", layer.normalStrength, 0f, 2f);

            // Texture preview
            if (layer.texture != null)
            {
                GUILayout.Label("Texture Preview:");
                Rect previewRect = GUILayoutUtility.GetRect(128, 128);
                EditorGUI.DrawPreviewTexture(previewRect, layer.texture);
            }
        }

        if (EditorGUI.EndChangeCheck() && autoUpdatePreview)
        {
            RegeneratePreview();
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawVegetationTab()
    {
        scrollPositionVegetation = EditorGUILayout.BeginScrollView(scrollPositionVegetation);

        EditorGUILayout.LabelField("Biome Vegetation", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Configure vegetation for the biome", MessageType.Info);

        EditorGUI.BeginChangeCheck();

        // Display vegetation list
        vegetationList.DoLayoutList();

        // If vegetation item is selected, show detailed settings
        int selectedVegIndex = vegetationList.index;
        if (selectedVegIndex >= 0 && selectedVegIndex < selectedBiome.vegetation.Count)
        {
            VegetationData vegData = selectedBiome.vegetation[selectedVegIndex];

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Vegetation Settings", EditorStyles.boldLabel);

            // Basic vegetation parameters
            vegData.prefabName = EditorGUILayout.TextField("Prefab Name", vegData.prefabName);
            vegData.prefab = (GameObject)EditorGUILayout.ObjectField("Prefab", vegData.prefab, typeof(GameObject), false);

            // Height range for placement
            EditorGUILayout.LabelField("Height Range for Placement");
            EditorGUILayout.BeginHorizontal();
            vegData.minHeight = EditorGUILayout.FloatField(vegData.minHeight, GUILayout.Width(50));
            EditorGUILayout.MinMaxSlider(ref vegData.minHeight, ref vegData.maxHeight, 0f, 1f);
            vegData.maxHeight = EditorGUILayout.FloatField(vegData.maxHeight, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            // Slope range for placement
            EditorGUILayout.LabelField("Slope Range for Placement");
            EditorGUILayout.BeginHorizontal();
            vegData.minSlope = EditorGUILayout.FloatField(vegData.minSlope, GUILayout.Width(50));
            EditorGUILayout.MinMaxSlider(ref vegData.minSlope, ref vegData.maxSlope, 0f, 1f);
            vegData.maxSlope = EditorGUILayout.FloatField(vegData.maxSlope, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            // Placement settings
            vegData.density = EditorGUILayout.Slider("Density", vegData.density, 0f, 1f);
            vegData.minScale = EditorGUILayout.FloatField("Minimum Scale", vegData.minScale);
            vegData.maxScale = EditorGUILayout.FloatField("Maximum Scale", vegData.maxScale);
            vegData.randomRotation = EditorGUILayout.Toggle("Random Rotation", vegData.randomRotation);
            vegData.alignToNormal = EditorGUILayout.Toggle("Align to Normal", vegData.alignToNormal);
            vegData.heightOffset = EditorGUILayout.FloatField("Height Offset", vegData.heightOffset);
            vegData.layer = EditorGUILayout.IntField("Layer", vegData.layer);

            // Prefab preview
            if (vegData.prefab != null)
            {
                GUILayout.Label("Prefab Preview:");
                Rect previewRect = GUILayoutUtility.GetRect(128, 128);

                // Use editor to display prefab preview
                Editor editor = Editor.CreateEditor(vegData.prefab);
                if (editor != null)
                {
                    editor.OnInteractivePreviewGUI(previewRect, EditorStyles.helpBox);
                    DestroyImmediate(editor);
                }
            }
        }

        if (EditorGUI.EndChangeCheck())
        {
            // No preview update needed here
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawPreviewTab()
    {
        EditorGUILayout.BeginVertical();

        EditorGUILayout.LabelField("Biome Preview", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        // Preview area
        Rect previewRect = GUILayoutUtility.GetRect(400, 300, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        if (Event.current.type == EventType.Repaint)
        {
            if (previewRenderer != null)
            {
                DrawPreview(previewRect);
            }
        }

        // Right settings panel
        EditorGUILayout.BeginVertical(GUILayout.Width(200));

        EditorGUILayout.LabelField("Preview Settings", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        // Noise generation settings for preview
        noiseScale = EditorGUILayout.Slider("Noise Scale", noiseScale, 0.01f, 0.1f);
        octaves = EditorGUILayout.IntSlider("Octaves", octaves, 1, 8);
        persistence = EditorGUILayout.Slider("Persistence", persistence, 0.1f, 1f);
        lacunarity = EditorGUILayout.Slider("Lacunarity", lacunarity, 1f, 4f);
        heightMult = EditorGUILayout.Slider("Height", heightMult, 1f, 30f);
        previewSize = EditorGUILayout.IntSlider("Size", previewSize, 32, 256);
        seed = EditorGUILayout.FloatField("Seed", seed);

        // Auto-update toggle
        autoUpdatePreview = EditorGUILayout.Toggle("Auto-Update", autoUpdatePreview);

        if (EditorGUI.EndChangeCheck() && autoUpdatePreview)
        {
            RegeneratePreview();
        }

        if (GUILayout.Button("Update Preview"))
        {
            RegeneratePreview();
        }

        EditorGUILayout.Space(10);

        // Biome map display toggle
        showBiomeMap = EditorGUILayout.Toggle("Show Biome Map", showBiomeMap);

        if (showBiomeMap)
        {
            if (biomeMapTexture == null)
            {
                GenerateBiomeMap();
            }

            if (biomeMapTexture != null)
            {
                Rect mapRect = GUILayoutUtility.GetRect(200, 200);
                EditorGUI.DrawPreviewTexture(mapRect, biomeMapTexture);

                if (GUILayout.Button("Update Biome Map"))
                {
                    GenerateBiomeMap();
                }
            }
        }

        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
    }

    private void DrawPreview(Rect rect)
    {
        if (previewRenderer == null || previewMesh == null || previewMaterial == null)
            return;

        // Configure preview camera
        previewRenderer.camera.transform.position = new Vector3(0, 20, -20);
        previewRenderer.camera.transform.rotation = Quaternion.Euler(45, 0, 0);

        previewRenderer.BeginPreview(rect, GUIStyle.none);

        // Draw preview mesh
        previewRenderer.DrawMesh(previewMesh, Matrix4x4.identity, previewMaterial, 0);

        // Display preview
        var texture = previewRenderer.EndPreview();
        GUI.DrawTexture(rect, texture);
    }

    private void GeneratePreviewMesh()
    {
        if (previewMesh != null)
        {
            DestroyImmediate(previewMesh);
        }

        previewMesh = new Mesh();

        int size = previewSize;
        int vertexCount = (size + 1) * (size + 1);
        int triangleCount = size * size * 6;
        
        // Ensure arrays are properly sized
        if (previewVertices.Length < vertexCount)
        {
            previewVertices = new Vector3[vertexCount];
            previewUVs = new Vector2[vertexCount];
        }
        
        if (previewTriangles.Length < triangleCount)
        {
            previewTriangles = new int[triangleCount];
        }

        // Create vertices and UV coordinates
        for (int z = 0; z <= size; z++)
        {
            for (int x = 0; x <= size; x++)
            {
                int index = z * (size + 1) + x;
                float normalizedX = x / (float)size;
                float normalizedZ = z / (float)size;

                // Generate noise with specified parameters
                float noiseValue = 0;
                float amplitude = 1;
                float frequency = 1;

                for (int o = 0; o < octaves; o++)
                {
                    float sampleX = (normalizedX * frequency + seed) * noiseScale;
                    float sampleZ = (normalizedZ * frequency + seed) * noiseScale;

                    float perlinValue = Mathf.PerlinNoise(sampleX, sampleZ) * 2 - 1;
                    noiseValue += perlinValue * amplitude;

                    amplitude *= persistence;
                    frequency *= lacunarity;
                }

                // Apply height curve if biome is selected
                if (selectedBiome != null)
                {
                    noiseValue = selectedBiome.heightCurve.Evaluate(noiseValue * 0.5f + 0.5f) * 2 - 1;
                }

                float yPos = noiseValue * heightMult;

                previewVertices[index] = new Vector3(x - size / 2, yPos, z - size / 2);
                previewUVs[index] = new Vector2(normalizedX, normalizedZ);
            }
        }

        // Create triangles
        int triangleIndex = 0;
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int bottomLeft = z * (size + 1) + x;
                int bottomRight = bottomLeft + 1;
                int topLeft = (z + 1) * (size + 1) + x;
                int topRight = topLeft + 1;

                // First triangle
                previewTriangles[triangleIndex++] = bottomLeft;
                previewTriangles[triangleIndex++] = topLeft;
                previewTriangles[triangleIndex++] = bottomRight;

                // Second triangle
                previewTriangles[triangleIndex++] = bottomRight;
                previewTriangles[triangleIndex++] = topLeft;
                previewTriangles[triangleIndex++] = topRight;
            }
        }

        // Apply data to mesh
        previewMesh.vertices = previewVertices;
        previewMesh.uv = previewUVs;
        previewMesh.triangles = previewTriangles;
        previewMesh.RecalculateNormals();
        previewMesh.RecalculateBounds();

        // Prepare material
        UpdatePreviewMaterial();
    }

    private void RegeneratePreview()
    {
        GeneratePreviewMesh();
    }

    private void UpdatePreviewMaterial()
    {
        if (previewMaterial == null || selectedBiome == null)
            return;

        // Configure material to display textures from biome
        if (selectedBiome.textureLayers.Count > 0 && selectedBiome.textureLayers[0].texture != null)
        {
            previewMaterial.mainTexture = selectedBiome.textureLayers[0].texture;
        }
        else
        {
            // Use default texture
            previewMaterial.mainTexture = Texture2D.grayTexture;
        }

        // Set material color according to biome
        previewMaterial.color = selectedBiome.editorColor;
    }

    private void GenerateBiomeMap()
    {
        if (biomeMapTexture != null)
        {
            DestroyImmediate(biomeMapTexture);
        }

        int mapSize = 256;
        biomeMapTexture = new Texture2D(mapSize, mapSize, TextureFormat.RGBA32, false);

        if (biomeManager == null)
        {
            // If no biome manager, create test map
            for (int y = 0; y < mapSize; y++)
            {
                for (int x = 0; x < mapSize; x++)
                {
                    float normX = x / (float)mapSize;
                    float normY = y / (float)mapSize;

                    float temperature = Mathf.PerlinNoise(normX * 3 + seed, normY * 3 + seed) * 0.5f + 0.25f;
                    float humidity = Mathf.PerlinNoise(normX * 3 + seed + 100, normY * 3 + seed + 100) * 0.5f + 0.25f;

                    // Find closest biome
                    BiomeData closestBiome = null;
                    float closestDistance = float.MaxValue;

                    foreach (var biome in biomes)
                    {
                        float tempDiff = Mathf.Abs(biome.temperature - temperature);
                        float humidityDiff = Mathf.Abs(biome.humidity - humidity);
                        float distance = tempDiff * tempDiff + humidityDiff * humidityDiff;

                        if (distance < closestDistance)
                        {
                            closestDistance = distance;
                            closestBiome = biome;
                        }
                    }

                    Color pixelColor = closestBiome != null ? closestBiome.editorColor : Color.black;
                    biomeMapTexture.SetPixel(x, y, pixelColor);
                }
            }
        }
        else
        {
            // If biome manager exists, use it for map generation
            float worldSize = 256 * 2; // World size
            float worldScale = worldSize / mapSize;

            for (int y = 0; y < mapSize; y++)
            {
                for (int x = 0; x < mapSize; x++)
                {
                    float worldX = (x - mapSize / 2) * worldScale;
                    float worldZ = (y - mapSize / 2) * worldScale;

                    Vector3 worldPos = new Vector3(worldX, 0, worldZ);
                    BiomeData biome = biomeManager.GetBiomeAt(worldPos);

                    Color pixelColor = biome != null ? biome.editorColor : Color.black;
                    biomeMapTexture.SetPixel(x, y, pixelColor);
                }
            }
        }

        biomeMapTexture.Apply();
    }

    private void LoadPresetBiome(BiomeType biomeType)
    {
        if (selectedBiomeIndex < 0) return;

        // Check if preset is cached first
        BiomeData presetBiome;
        if (presetCache.TryGetValue(biomeType, out presetBiome))
        {
            presetBiome = presetBiome.Clone(); // Clone cached preset
        }
        else
        {
            // Load preset for selected biome type
            presetBiome = BiomeData.GetDefaultBiome(biomeType);
            presetCache[biomeType] = presetBiome.Clone(); // Cache the preset
        }

        // Copy preset data to current biome, preserving id
        string currentId = selectedBiome.id;
        string currentName = selectedBiome.name;

        selectedBiome.type = presetBiome.type;
        selectedBiome.editorColor = presetBiome.editorColor;
        selectedBiome.temperature = presetBiome.temperature;
        selectedBiome.humidity = presetBiome.humidity;
        selectedBiome.baseHeight = presetBiome.baseHeight;
        selectedBiome.snowHeight = presetBiome.snowHeight;
        selectedBiome.noiseSettings = new NoiseFunctions.NoiseSettings(
            presetBiome.noiseSettings.noiseType,
            presetBiome.noiseSettings.scale,
            presetBiome.noiseSettings.octaves,
            presetBiome.noiseSettings.persistence,
            presetBiome.noiseSettings.lacunarity,
            presetBiome.noiseSettings.offset);
        selectedBiome.heightMultiplier = presetBiome.heightMultiplier;
        selectedBiome.slopeMultiplier = presetBiome.slopeMultiplier;
        selectedBiome.textureScale = presetBiome.textureScale;
        selectedBiome.blendStrength = presetBiome.blendStrength;
        selectedBiome.vegetationDensity = presetBiome.vegetationDensity;

        // Copy height curve
        selectedBiome.heightCurve = new AnimationCurve();
        foreach (var key in presetBiome.heightCurve.keys)
        {
            selectedBiome.heightCurve.AddKey(key);
        }

        // Copy texture layers
        selectedBiome.textureLayers.Clear();
        foreach (var layer in presetBiome.textureLayers)
        {
            selectedBiome.textureLayers.Add(layer.Clone());
        }

        // Copy vegetation data
        selectedBiome.vegetation.Clear();
        foreach (var veg in presetBiome.vegetation)
        {
            selectedBiome.vegetation.Add(veg.Clone());
        }

        // Restore id and name
        selectedBiome.id = currentId;
        selectedBiome.name = currentName;

        // Update lists
        textureLayersList.list = selectedBiome.textureLayers;
        vegetationList.list = selectedBiome.vegetation;

        // Update preview
        if (autoUpdatePreview) RegeneratePreview();
    }

    private void SaveBiomePresets()
    {
        // Create presets folder if it doesn't exist
        if (!AssetDatabase.IsValidFolder(BIOME_PRESETS_FOLDER))
        {
            string parentFolder = Path.GetDirectoryName(BIOME_PRESETS_FOLDER);
            string folderName = Path.GetFileName(BIOME_PRESETS_FOLDER);
            AssetDatabase.CreateFolder(parentFolder, folderName);
        }

        foreach (var biome in biomes)
        {
            // Save each biome as ScriptableObject
            string path = $"{BIOME_PRESETS_FOLDER}/{biome.name}_{biome.type}.asset";

            // Check if file already exists
            BiomeData existingBiome = AssetDatabase.LoadAssetAtPath<BiomeData>(path);

            if (existingBiome != null)
            {
                // Update existing biome
                EditorUtility.CopySerialized(biome, existingBiome);
                EditorUtility.SetDirty(existingBiome);
            }
            else
            {
                // Create new biome asset
                AssetDatabase.CreateAsset(biome, path);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Saved {biomes.Count} biomes to folder {BIOME_PRESETS_FOLDER}");
    }

    private void LoadBiomePresets()
    {
        // Load all saved biomes
        if (!AssetDatabase.IsValidFolder(BIOME_PRESETS_FOLDER))
        {
            Debug.LogWarning("Presets folder not found!");
            return;
        }

        // Get all files in folder
        string[] guids = AssetDatabase.FindAssets("t:BiomeData", new[] { BIOME_PRESETS_FOLDER });

        if (guids.Length == 0)
        {
            Debug.LogWarning("Biome presets not found!");
            return;
        }

        // Clear current biomes and load saved ones
        biomes.Clear();

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            BiomeData biome = AssetDatabase.LoadAssetAtPath<BiomeData>(path);

            if (biome != null)
            {
                biomes.Add(biome);
            }
        }

        // Set first biome as selected
        if (biomes.Count > 0)
        {
            selectedBiomeIndex = 0;
            selectedBiome = biomes[0];

            textureLayersList.list = selectedBiome.textureLayers;
            vegetationList.list = selectedBiome.vegetation;

            if (autoUpdatePreview) RegeneratePreview();
        }

        Debug.Log($"Loaded {biomes.Count} biomes");
    }

    private void ApplyToScene()
    {
        if (biomeManager == null)
        {
            Debug.LogWarning("BiomeManager not found in scene!");
            return;
        }

        // Copy biomes to manager
        var serializedManager = new SerializedObject(biomeManager);
        var biomesProp = serializedManager.FindProperty("biomes");

        if (biomesProp != null)
        {
            biomesProp.ClearArray();

            for (int i = 0; i < biomes.Count; i++)
            {
                biomesProp.arraySize++;
                var element = biomesProp.GetArrayElementAtIndex(i);
                element.objectReferenceValue = biomes[i];
            }

            // Set default biome
            var defaultBiomeProp = serializedManager.FindProperty("defaultBiome");
            if (defaultBiomeProp != null && biomes.Count > 0)
            {
                defaultBiomeProp.objectReferenceValue = biomes[0];
            }

            serializedManager.ApplyModifiedProperties();

            // Update shader data if method exists
            var updateShaderMethod = biomeManager.GetType().GetMethod("UpdateShaderWithBiomeData");
            if (updateShaderMethod != null)
            {
                updateShaderMethod.Invoke(biomeManager, null);
            }

            Debug.Log("Biomes successfully applied to scene!");
        }
    }
}
#endif