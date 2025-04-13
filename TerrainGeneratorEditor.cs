using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Mono.Cecil;

/// <summary>
/// Custom editor for the TerrainGenerator component
/// Provides a streamlined interface with auto-regeneration capabilities
/// </summary>
[CustomEditor(typeof(TerrainGenerator))]
public class TerrainGeneratorEditor : Editor
{
    private TerrainGenerator terrainGenerator;
    private SerializedProperty xSizeProp;
    private SerializedProperty zSizeProp;
    private SerializedProperty chunkSizeProp;
    private SerializedProperty xOffSetProp;
    private SerializedProperty zOffSetProp;
    private SerializedProperty noiseScaleProp;
    private SerializedProperty heightMultiplerProp;
    private SerializedProperty octavesCountProp;
    private SerializedProperty lacunarityProp;
    private SerializedProperty persistanceProp;
    private SerializedProperty noiseTypeProp;
    private SerializedProperty seedProp;
    private SerializedProperty heightCurveProp;

    // LOD Settings
    private SerializedProperty enableLODProp;
    private SerializedProperty lodDistancesProp;

    // Performance settings
    private SerializedProperty useObjectPoolingProp;
    private SerializedProperty useBackgroundGenerationProp;

    // UI state tracking
    private bool showPerformanceSettings = false;
    private bool showNoiseSettings = true;
    private bool showLODSettings = false;
    private bool showTextureSettings = false;

    // Delayed generation for better UX
    private bool needsRegeneration = false;
    private double lastChangeTime;
    private const double REGEN_DELAY = 0.5; // 500ms delay

    private void OnEnable()
    {
        terrainGenerator = (TerrainGenerator)target;

        // Cache serialized properties for better performance
        terrainGenerator = (TerrainGenerator)target;
        xSizeProp = serializedObject.FindProperty("xSize");
        zSizeProp = serializedObject.FindProperty("zSize");
        chunkSizeProp = serializedObject.FindProperty("chunkSize");
        xOffSetProp = serializedObject.FindProperty("xOffSet");
        zOffSetProp = serializedObject.FindProperty("zOffSet");
        noiseScaleProp = serializedObject.FindProperty("noiseScale");
        heightMultiplerProp = serializedObject.FindProperty("heightMultipler");
        octavesCountProp = serializedObject.FindProperty("octavesCount");
        lacunarityProp = serializedObject.FindProperty("lacunarity");
        persistanceProp = serializedObject.FindProperty("persistance");
        noiseTypeProp = serializedObject.FindProperty("noiseType");
        seedProp = serializedObject.FindProperty("seed");
        heightCurveProp = serializedObject.FindProperty("heightCurve");

        // LOD properties
        enableLODProp = serializedObject.FindProperty("enableLOD");
        lodDistancesProp = serializedObject.FindProperty("lodDistances");

        // Performance properties
        useObjectPoolingProp = serializedObject.FindProperty("useObjectPooling");
        useBackgroundGenerationProp = serializedObject.FindProperty("useBackgroundGeneration");

        // Register for Editor updates so we can handle delayed regeneration
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        // Unregister from Editor updates
        EditorApplication.update -= OnEditorUpdate;
    }

    /// <summary>
    /// Handle delayed terrain regeneration
    /// </summary>
    private void OnEditorUpdate()
    {
        // If regeneration is needed and delay has passed
        if (needsRegeneration && EditorApplication.timeSinceStartup - lastChangeTime > REGEN_DELAY)
        {
            needsRegeneration = false;
            terrainGenerator.GenerateTerrain();
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUI.BeginChangeCheck();

        // Logo or title section
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Procedural Terrain Generator", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Basic settings
        EditorGUILayout.LabelField("Terrain Dimensions", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(xSizeProp, new GUIContent("Width (X)"));
        EditorGUILayout.PropertyField(zSizeProp, new GUIContent("Length (Z)"));
        EditorGUILayout.PropertyField(chunkSizeProp, new GUIContent("Chunk Size"));

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(xOffSetProp, new GUIContent("X Offset"));
        EditorGUILayout.PropertyField(zOffSetProp, new GUIContent("Z Offset"));

        // Noise settings in a foldout
        showNoiseSettings = EditorGUILayout.Foldout(showNoiseSettings, "Noise Settings", true);
        if (showNoiseSettings)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(noiseTypeProp, new GUIContent("Noise Type"));
            EditorGUILayout.PropertyField(noiseScaleProp, new GUIContent("Noise Scale"));
            EditorGUILayout.PropertyField(heightMultiplerProp, new GUIContent("Height Multiplier"));
            EditorGUILayout.PropertyField(octavesCountProp, new GUIContent("Octaves"));
            EditorGUILayout.PropertyField(lacunarityProp, new GUIContent("Lacunarity"));
            EditorGUILayout.PropertyField(persistanceProp, new GUIContent("Persistence"));
            EditorGUILayout.PropertyField(seedProp, new GUIContent("Seed"));
            EditorGUILayout.PropertyField(heightCurveProp, new GUIContent("Height Curve"));

            if (GUILayout.Button("Randomize Seed"))
            {
                Undo.RecordObject(terrainGenerator, "Randomize Terrain Seed");
                seedProp.intValue = Random.Range(0, 10000);
                serializedObject.ApplyModifiedProperties();
                terrainGenerator.GenerateTerrain();
            }

            EditorGUI.indentLevel--;
        }

        // LOD Settings
        showLODSettings = EditorGUILayout.Foldout(showLODSettings, "LOD Settings", true);
        if (showLODSettings)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(enableLODProp, new GUIContent("Enable LOD"));

            if (enableLODProp.boolValue)
            {
                EditorGUILayout.PropertyField(lodDistancesProp, new GUIContent("LOD Distances"));
            }

            EditorGUI.indentLevel--;
        }

        // Performance settings
        showPerformanceSettings = EditorGUILayout.Foldout(showPerformanceSettings, "Performance Settings", true);
        if (showPerformanceSettings)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(useObjectPoolingProp, new GUIContent("Use Object Pooling"));
            EditorGUILayout.PropertyField(useBackgroundGenerationProp, new GUIContent("Background Generation"));

            // Display info about these settings
            EditorGUILayout.HelpBox(
                "Object Pooling: Reuse chunk GameObjects for better performance\n" +
                "Background Generation: Generate chunks asynchronously to prevent freezing",
                MessageType.Info);

            EditorGUI.indentLevel--;
        }

        // Texture settings if needed
        showTextureSettings = EditorGUILayout.Foldout(showTextureSettings, "Texture Settings", true);
        if (showTextureSettings)
        {
            // Draw default inspector for texture properties
            DrawPropertiesExcluding(serializedObject, new string[] {
                "xSize", "zSize", "chunkSize", "xOffSet", "zOffSet",
                "noiseScale", "heightMultipler", "octavesCount", "lacunarity",
                "persistance", "noiseType", "seed", "heightCurve",
                "enableLOD", "lodDistances",
                "useObjectPooling", "useBackgroundGeneration"
            });
        }

        // Apply changes to serialized properties
        serializedObject.ApplyModifiedProperties();

        // If properties were changed, queue terrain regeneration with delay
        if (EditorGUI.EndChangeCheck())
        {
            needsRegeneration = true;
            lastChangeTime = EditorApplication.timeSinceStartup;
        }

        // Generate button
        EditorGUILayout.Space();
        if (GUILayout.Button("Generate Terrain"))
        {
            needsRegeneration = false; // Cancel any pending regeneration
            terrainGenerator.GenerateTerrain();
        }

        // Progress indicator if generation is happening
        if (needsRegeneration)
        {
            Rect progressRect = EditorGUILayout.GetControlRect(false, 20);
            float progress = Mathf.Clamp01((float)(EditorApplication.timeSinceStartup - lastChangeTime) / (float)REGEN_DELAY);
            EditorGUI.ProgressBar(progressRect, progress, "Generating...");
        }
    }

    /// <summary>
    /// Draw additional gizmos in the scene view
    /// </summary>
    [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
    private static void DrawGizmos(TerrainGenerator generator, GizmoType gizmoType)
    {
        if (generator == null) return;

        Gizmos.color = Color.green;

        // Получаем значения через SerializedProperty для безопасности
        SerializedObject so = new SerializedObject(generator);
        int xSize = so.FindProperty("xSize").intValue;
        int zSize = so.FindProperty("zSize").intValue;
        int chunkSize = so.FindProperty("chunkSize").intValue;

        float width = chunkSize * Mathf.CeilToInt(xSize / (float)chunkSize);
        float length = chunkSize * Mathf.CeilToInt(zSize / (float)chunkSize);

        Vector3 center = generator.transform.position + new Vector3(width / 2, 0, length / 2);
        Gizmos.DrawWireCube(center, new Vector3(width, 10, length));
    }
}