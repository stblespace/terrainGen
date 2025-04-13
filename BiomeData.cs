using System;
using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;
using UnityEngine.Serialization;

namespace TerrainGeneration
{
    /// <summary>
    /// Перечисление для определения типа биома
    /// </summary>
    public enum BiomeType
    {
        Plains,     // Равнины
        Desert,     // Пустыня
        Mountains,  // Горы
        Forest,     // Лес
        Swamp,      // Болото
        Tundra,     // Тундра
        Taiga,      // Тайга
        Savanna,    // Саванна
        Jungle,     // Джунгли
        Custom      // Пользовательский биом
    }

    /// <summary>
    /// Класс данных для описания биома
    /// Содержит все параметры, необходимые для генерации ландшафта с определенным типом местности
    /// </summary>
    [System.Serializable]
    public class BiomeData : ScriptableObject, IEquatable<BiomeData>
    {
        // Базовые параметры
        [Tooltip("Уникальный идентификатор биома")]
        public string id = Guid.NewGuid().ToString();

        [Tooltip("Название биома")]
        public new string name = "Новый биом";

        [Tooltip("Тип биома")]
        public BiomeType type = BiomeType.Plains;

        [Tooltip("Цвет для отображения биома в редакторе")]
        public Color editorColor = Color.green;

        [Header("Параметры климата")]
        [Range(0f, 1f)]
        [Tooltip("Температура биома (0 - холодно, 1 - жарко)")]
        public float temperature = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Влажность биома (0 - сухо, 1 - влажно)")]
        public float humidity = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Базовая высота биома (0 - низко, 1 - высоко)")]
        public float baseHeight = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Высота снега (% от максимальной высоты)")]
        public float snowHeight = 0.8f;

        [Header("Параметры шума")]
        [Tooltip("Настройки шума для биома")]
        public NoiseFunctions.NoiseSettings noiseSettings = new NoiseFunctions.NoiseSettings(
            NoiseFunctions.NoiseType.FBM, 0.03f, 4, 0.5f, 2.0f, 0f);

        [Tooltip("Множитель высоты для биома")]
        public float heightMultiplier = 10f;

        [Range(0f, 2f)]
        [Tooltip("Крутизна склонов (0 - пологие, 2 - крутые)")]
        public float slopeMultiplier = 1f;

        [Tooltip("Кривая высоты для ландшафта")]
        public AnimationCurve heightCurve = AnimationCurve.Linear(0, 0, 1, 1);

        [Header("Параметры текстурирования")]
        [Tooltip("Слои текстур для биома")]
        public List<TerrainTextureLayer> textureLayers = new List<TerrainTextureLayer>();

        [Range(0.1f, 10f)]
        [Tooltip("Масштаб текстур")]
        public float textureScale = 1f;

        [Range(0f, 1f)]
        [Tooltip("Сила смешивания текстур на границах биомов")]
        public float blendStrength = 0.5f;

        [Header("Параметры растительности")]
        [Tooltip("Данные о растительности для биома")]
        public List<VegetationData> vegetation = new List<VegetationData>();

        [Range(0f, 1f)]
        [Tooltip("Плотность растительности")]
        public float vegetationDensity = 0.5f;

        // Кеш для быстрого доступа к настройкам биомов
        private static readonly Dictionary<BiomeType, BiomeData> biomeCache = new Dictionary<BiomeType, BiomeData>();
        
        // Объект блокировки для потокобезопасного доступа к кешу
        private static readonly object cacheLock = new object();

        /// <summary>
        /// Создает новый экземпляр BiomeData
        /// </summary>
        /// <param name="biomeName">Название биома</param>
        /// <returns>Новый экземпляр BiomeData</returns>
        public static new BiomeData CreateInstance(string biomeName = "Новый биом")
        {
            BiomeData biome = ScriptableObject.CreateInstance<BiomeData>();
            biome.name = biomeName;
            return biome;
        }

        /// <summary>
        /// Создает глубокую копию объекта BiomeData
        /// </summary>
        /// <returns>Копия текущего биома</returns>
        public BiomeData Clone()
        {
            var clone = CreateInstance(name);

            // Копирование базовых свойств
            clone.id = id;
            clone.type = type;
            clone.editorColor = editorColor;
            clone.temperature = temperature;
            clone.humidity = humidity;
            clone.baseHeight = baseHeight;
            clone.snowHeight = snowHeight;
            clone.heightMultiplier = heightMultiplier;
            clone.slopeMultiplier = slopeMultiplier;
            clone.textureScale = textureScale;
            clone.blendStrength = blendStrength;
            clone.vegetationDensity = vegetationDensity;

            // Создание копии настроек шума
            clone.noiseSettings = new NoiseFunctions.NoiseSettings(
                noiseSettings.noiseType,
                noiseSettings.scale,
                noiseSettings.octaves,
                noiseSettings.persistence,
                noiseSettings.lacunarity,
                noiseSettings.offset);

            // Копирование кривой высоты
            clone.heightCurve = new AnimationCurve();
            foreach (var key in heightCurve.keys)
            {
                // Создание новой ключевой точки для правильного клонирования
                clone.heightCurve.AddKey(new Keyframe(
                    key.time, 
                    key.value, 
                    key.inTangent, 
                    key.outTangent, 
                    key.inWeight, 
                    key.outWeight)
                );
            }

            // Копирование слоев текстур
            clone.textureLayers = new List<TerrainTextureLayer>(textureLayers.Count);
            foreach (var layer in textureLayers)
            {
                clone.textureLayers.Add(layer.Clone());
            }

            // Копирование данных о растительности
            clone.vegetation = new List<VegetationData>(vegetation.Count);
            foreach (var veg in vegetation)
            {
                clone.vegetation.Add(veg.Clone());
            }

            return clone;
        }

        /// <summary>
        /// Возвращает данные биома указанного типа с предустановленными настройками
        /// </summary>
        /// <param name="biomeType">Тип биома</param>
        /// <returns>Новый экземпляр биома указанного типа</returns>
        public static BiomeData GetDefaultBiome(BiomeType biomeType)
        {
            lock (cacheLock)
            {
                // Проверяем наличие биома в кеше
                if (biomeCache.TryGetValue(biomeType, out BiomeData cachedBiome))
                {
                    return cachedBiome.Clone();
                }
            }

            BiomeData biome = CreateInstance(biomeType.ToString());
            biome.type = biomeType;

            switch (biomeType)
            {
                case BiomeType.Plains:
                    ConfigurePlains(biome);
                    break;
                case BiomeType.Desert:
                    ConfigureDesert(biome);
                    break;
                case BiomeType.Mountains:
                    ConfigureMountains(biome);
                    break;
                case BiomeType.Forest:
                    ConfigureForest(biome);
                    break;
                case BiomeType.Swamp:
                    ConfigureSwamp(biome);
                    break;
                case BiomeType.Tundra:
                    ConfigureTundra(biome);
                    break;
                case BiomeType.Taiga:
                    ConfigureTaiga(biome);
                    break;
                case BiomeType.Savanna:
                    ConfigureSavanna(biome);
                    break;
                case BiomeType.Jungle:
                    ConfigureJungle(biome);
                    break;
                default:
                    ConfigurePlains(biome);
                    break;
            }

            lock (cacheLock)
            {
                // Кешируем биом для дальнейшего использования
                biomeCache[biomeType] = biome.Clone();
            }

            return biome;
        }

        /// <summary>
        /// Очищает кеш биомов для освобождения памяти
        /// </summary>
        public static void ClearCache()
        {
            lock (cacheLock)
            {
                biomeCache.Clear();
            }
        }

        #region Методы конфигурации биомов

        /// <summary>
        /// Настройка биома типа "Равнины"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigurePlains(BiomeData biome)
        {
            biome.name = "Равнины";
            biome.editorColor = new Color(0.7f, 0.9f, 0.3f);
            biome.temperature = 0.5f;
            biome.humidity = 0.4f;
            biome.baseHeight = 0.2f;
            biome.snowHeight = 0.9f;
            biome.heightMultiplier = 5f;
            biome.slopeMultiplier = 0.5f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.FBM, 0.02f, 3, 0.4f, 2.0f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.4f, 0.2f),
                new Keyframe(0.7f, 0.4f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.1f,
                    slopeStart = 0f,
                    slopeEnd = 0.3f,
                    textureName = "Grass"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.05f,
                    heightEnd = 0.3f,
                    slopeStart = 0.25f,
                    slopeEnd = 0.7f,
                    textureName = "Dirt"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.25f,
                    heightEnd = 1f,
                    slopeStart = 0.6f,
                    slopeEnd = 1f,
                    textureName = "Rock"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Grass",
                    minHeight = 0.1f,
                    maxHeight = 0.5f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.8f,
                    minScale = 0.8f,
                    maxScale = 1.2f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Tree_Oak",
                    minHeight = 0.2f,
                    maxHeight = 0.6f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.2f,
                    minScale = 0.8f,
                    maxScale = 1.5f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Пустыня"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureDesert(BiomeData biome)
        {
            biome.name = "Пустыня";
            biome.editorColor = new Color(0.95f, 0.9f, 0.6f);
            biome.temperature = 0.9f;
            biome.humidity = 0.1f;
            biome.baseHeight = 0.2f;
            biome.snowHeight = 1.0f; // Нет снега в пустыне
            biome.heightMultiplier = 4f;
            biome.slopeMultiplier = 0.3f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.Perlin, 0.03f, 2, 0.6f, 2.0f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.3f, 0.1f),
                new Keyframe(0.6f, 0.2f),
                new Keyframe(0.7f, 0.3f),
                new Keyframe(0.9f, 0.8f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.8f,
                    slopeStart = 0f,
                    slopeEnd = 0.4f,
                    textureName = "Sand"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.4f,
                    heightEnd = 1f,
                    slopeStart = 0.3f,
                    slopeEnd = 1f,
                    textureName = "DesertRock"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Cactus",
                    minHeight = 0.1f,
                    maxHeight = 0.6f,
                    minSlope = 0f,
                    maxSlope = 0.2f,
                    density = 0.1f,
                    minScale = 0.7f,
                    maxScale = 1.3f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "DesertBush",
                    minHeight = 0.1f,
                    maxHeight = 0.5f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.15f,
                    minScale = 0.8f,
                    maxScale = 1.2f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Горы"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureMountains(BiomeData biome)
        {
            biome.name = "Горы";
            biome.editorColor = new Color(0.6f, 0.6f, 0.6f);
            biome.temperature = 0.3f;
            biome.humidity = 0.4f;
            biome.baseHeight = 0.6f;
            biome.snowHeight = 0.7f;
            biome.heightMultiplier = 20f;
            biome.slopeMultiplier = 1.5f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.Ridged, 0.02f, 5, 0.5f, 2.5f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.5f, 0.3f),
                new Keyframe(0.7f, 0.5f),
                new Keyframe(0.85f, 0.7f),
                new Keyframe(0.95f, 0.9f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.3f,
                    slopeStart = 0f,
                    slopeEnd = 0.4f,
                    textureName = "Grass"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.2f,
                    heightEnd = 0.6f,
                    slopeStart = 0.3f,
                    slopeEnd = 0.8f,
                    textureName = "MountainDirt"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.5f,
                    heightEnd = 0.85f,
                    slopeStart = 0.6f,
                    slopeEnd = 1f,
                    textureName = "Rock"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.75f,
                    heightEnd = 1f,
                    slopeStart = 0f,
                    slopeEnd = 1f,
                    textureName = "Snow"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Tree_Pine",
                    minHeight = 0.2f,
                    maxHeight = 0.5f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.3f,
                    minScale = 0.8f,
                    maxScale = 1.4f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Rock_Small",
                    minHeight = 0.3f,
                    maxHeight = 0.9f,
                    minSlope = 0.1f,
                    maxSlope = 0.7f,
                    density = 0.4f,
                    minScale = 0.5f,
                    maxScale = 2.0f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Лес"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureForest(BiomeData biome)
        {
            biome.name = "Лес";
            biome.editorColor = new Color(0.2f, 0.6f, 0.2f);
            biome.temperature = 0.5f;
            biome.humidity = 0.7f;
            biome.baseHeight = 0.3f;
            biome.snowHeight = 0.85f;
            biome.heightMultiplier = 8f;
            biome.slopeMultiplier = 0.8f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.FBM, 0.025f, 4, 0.45f, 2.0f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.3f, 0.2f),
                new Keyframe(0.6f, 0.6f),
                new Keyframe(0.8f, 0.8f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.6f,
                    slopeStart = 0f,
                    slopeEnd = 0.4f,
                    textureName = "ForestFloor"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.3f,
                    heightEnd = 0.8f,
                    slopeStart = 0.3f,
                    slopeEnd = 0.8f,
                    textureName = "Dirt"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.7f,
                    heightEnd = 1f,
                    slopeStart = 0.7f,
                    slopeEnd = 1f,
                    textureName = "Rock"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Tree_Oak",
                    minHeight = 0.1f,
                    maxHeight = 0.6f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.5f,
                    minScale = 0.8f,
                    maxScale = 1.5f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Tree_Pine",
                    minHeight = 0.2f,
                    maxHeight = 0.7f,
                    minSlope = 0f,
                    maxSlope = 0.4f,
                    density = 0.3f,
                    minScale = 0.7f,
                    maxScale = 1.3f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Bush",
                    minHeight = 0.1f,
                    maxHeight = 0.5f,
                    minSlope = 0f,
                    maxSlope = 0.5f,
                    density = 0.6f,
                    minScale = 0.6f,
                    maxScale = 1.2f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Болото"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureSwamp(BiomeData biome)
        {
            biome.name = "Болото";
            biome.editorColor = new Color(0.4f, 0.5f, 0.2f);
            biome.temperature = 0.6f;
            biome.humidity = 0.9f;
            biome.baseHeight = 0.1f;
            biome.snowHeight = 1.0f; // Нет снега в болоте
            biome.heightMultiplier = 3f;
            biome.slopeMultiplier = 0.3f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.Simplex, 0.04f, 3, 0.6f, 1.8f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.5f, 0.1f),
                new Keyframe(0.7f, 0.15f),
                new Keyframe(0.9f, 0.3f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.3f,
                    slopeStart = 0f,
                    slopeEnd = 0.3f,
                    textureName = "SwampMud"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.2f,
                    heightEnd = 0.6f,
                    slopeStart = 0.2f,
                    slopeEnd = 0.5f,
                    textureName = "SwampGrass"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.5f,
                    heightEnd = 1f,
                    slopeStart = 0.4f,
                    slopeEnd = 1f,
                    textureName = "Dirt"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Tree_Willow",
                    minHeight = 0.1f,
                    maxHeight = 0.4f,
                    minSlope = 0f,
                    maxSlope = 0.2f,
                    density = 0.2f,
                    minScale = 0.8f,
                    maxScale = 1.4f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Reed",
                    minHeight = 0.05f,
                    maxHeight = 0.2f,
                    minSlope = 0f,
                    maxSlope = 0.15f,
                    density = 0.7f,
                    minScale = 0.7f,
                    maxScale = 1.3f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "SwampPlant",
                    minHeight = 0.05f,
                    maxHeight = 0.3f,
                    minSlope = 0f,
                    maxSlope = 0.2f,
                    density = 0.5f,
                    minScale = 0.6f,
                    maxScale = 1.1f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Тундра"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureTundra(BiomeData biome)
        {
            biome.name = "Тундра";
            biome.editorColor = new Color(0.8f, 0.85f, 0.9f);
            biome.temperature = 0.1f;
            biome.humidity = 0.3f;
            biome.baseHeight = 0.25f;
            biome.snowHeight = 0.5f;
            biome.heightMultiplier = 6f;
            biome.slopeMultiplier = 0.7f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.Perlin, 0.03f, 3, 0.5f, 2.0f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.4f, 0.2f),
                new Keyframe(0.7f, 0.5f),
                new Keyframe(0.9f, 0.7f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.4f,
                    slopeStart = 0f,
                    slopeEnd = 0.3f,
                    textureName = "TundraGround"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.3f,
                    heightEnd = 0.6f,
                    slopeStart = 0.2f,
                    slopeEnd = 0.6f,
                    textureName = "TundraDirt"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.5f,
                    heightEnd = 1f,
                    slopeStart = 0.5f,
                    slopeEnd = 1f,
                    textureName = "Rock"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.5f,
                    heightEnd = 1f,
                    slopeStart = 0f,
                    slopeEnd = 0.5f,
                    textureName = "Snow"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "TundraGrass",
                    minHeight = 0.1f,
                    maxHeight = 0.4f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.4f,
                    minScale = 0.7f,
                    maxScale = 1.2f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Rock_Small",
                    minHeight = 0.2f,
                    maxHeight = 0.8f,
                    minSlope = 0f,
                    maxSlope = 0.6f,
                    density = 0.3f,
                    minScale = 0.5f,
                    maxScale = 1.5f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Тайга"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureTaiga(BiomeData biome)
        {
            biome.name = "Тайга";
            biome.editorColor = new Color(0.3f, 0.5f, 0.3f);
            biome.temperature = 0.3f;
            biome.humidity = 0.6f;
            biome.baseHeight = 0.35f;
            biome.snowHeight = 0.7f;
            biome.heightMultiplier = 7f;
            biome.slopeMultiplier = 0.9f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.FBM, 0.025f, 4, 0.4f, 2.2f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.3f, 0.2f),
                new Keyframe(0.6f, 0.5f),
                new Keyframe(0.8f, 0.7f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.5f,
                    slopeStart = 0f,
                    slopeEnd = 0.4f,
                    textureName = "TaigaGround"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.4f,
                    heightEnd = 0.7f,
                    slopeStart = 0.3f,
                    slopeEnd = 0.7f,
                    textureName = "Dirt"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.6f,
                    heightEnd = 0.9f,
                    slopeStart = 0.6f,
                    slopeEnd = 1f,
                    textureName = "Rock"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.75f,
                    heightEnd = 1f,
                    slopeStart = 0f,
                    slopeEnd = 1f,
                    textureName = "Snow"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Tree_Pine",
                    minHeight = 0.2f,
                    maxHeight = 0.6f,
                    minSlope = 0f,
                    maxSlope = 0.4f,
                    density = 0.6f,
                    minScale = 0.8f,
                    maxScale = 1.5f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Tree_Spruce",
                    minHeight = 0.2f,
                    maxHeight = 0.6f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.4f,
                    minScale = 0.7f,
                    maxScale = 1.4f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "Bush_Pine",
                    minHeight = 0.1f,
                    maxHeight = 0.5f,
                    minSlope = 0f,
                    maxSlope = 0.5f,
                    density = 0.3f,
                    minScale = 0.6f,
                    maxScale = 1.1f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Саванна"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureSavanna(BiomeData biome)
        {
            biome.name = "Саванна";
            biome.editorColor = new Color(0.85f, 0.7f, 0.3f);
            biome.temperature = 0.8f;
            biome.humidity = 0.3f;
            biome.baseHeight = 0.25f;
            biome.snowHeight = 1.0f; // Нет снега в саванне
            biome.heightMultiplier = 6f;
            biome.slopeMultiplier = 0.6f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.Perlin, 0.03f, 3, 0.5f, 2.0f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.4f, 0.15f),
                new Keyframe(0.6f, 0.3f),
                new Keyframe(0.8f, 0.6f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.6f,
                    slopeStart = 0f,
                    slopeEnd = 0.4f,
                    textureName = "SavannaGrass"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.4f,
                    heightEnd = 0.8f,
                    slopeStart = 0.3f,
                    slopeEnd = 0.7f,
                    textureName = "SavannaDirt"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.7f,
                    heightEnd = 1f,
                    slopeStart = 0.6f,
                    slopeEnd = 1f,
                    textureName = "Rock"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Tree_Acacia",
                    minHeight = 0.2f,
                    maxHeight = 0.5f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.15f,
                    minScale = 0.8f,
                    maxScale = 1.4f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "SavannaGrass",
                    minHeight = 0.1f,
                    maxHeight = 0.6f,
                    minSlope = 0f,
                    maxSlope = 0.4f,
                    density = 0.6f,
                    minScale = 0.7f,
                    maxScale = 1.2f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "SavannaBush",
                    minHeight = 0.1f,
                    maxHeight = 0.4f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.3f,
                    minScale = 0.6f,
                    maxScale = 1.1f,
                    randomRotation = true
                }
            };
        }

        /// <summary>
        /// Настройка биома типа "Джунгли"
        /// </summary>
        /// <param name="biome">Биом для настройки</param>
        private static void ConfigureJungle(BiomeData biome)
        {
            biome.name = "Джунгли";
            biome.editorColor = new Color(0.1f, 0.6f, 0.1f);
            biome.temperature = 0.9f;
            biome.humidity = 0.9f;
            biome.baseHeight = 0.3f;
            biome.snowHeight = 1.0f; // Нет снега в джунглях
            biome.heightMultiplier = 12f;
            biome.slopeMultiplier = 1.0f;

            biome.noiseSettings = new NoiseFunctions.NoiseSettings(
                NoiseFunctions.NoiseType.FBM, 0.03f, 4, 0.5f, 2.2f);

            // Настройка кривой высоты
            biome.heightCurve = new AnimationCurve(
                new Keyframe(0, 0),
                new Keyframe(0.3f, 0.2f),
                new Keyframe(0.5f, 0.3f),
                new Keyframe(0.7f, 0.6f),
                new Keyframe(0.9f, 0.9f),
                new Keyframe(1, 1)
            );

            // Слои текстур
            biome.textureLayers = new List<TerrainTextureLayer>
            {
                new TerrainTextureLayer
                {
                    heightStart = 0f,
                    heightEnd = 0.6f,
                    slopeStart = 0f,
                    slopeEnd = 0.4f,
                    textureName = "JungleGrass"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.4f,
                    heightEnd = 0.8f,
                    slopeStart = 0.3f,
                    slopeEnd = 0.7f,
                    textureName = "JungleDirt"
                },
                new TerrainTextureLayer
                {
                    heightStart = 0.7f,
                    heightEnd = 1f,
                    slopeStart = 0.6f,
                    slopeEnd = 1f,
                    textureName = "Rock"
                }
            };

            // Настройка растительности
            biome.vegetation = new List<VegetationData>
            {
                new VegetationData
                {
                    prefabName = "Tree_Jungle",
                    minHeight = 0.2f,
                    maxHeight = 0.6f,
                    minSlope = 0f,
                    maxSlope = 0.3f,
                    density = 0.6f,
                    minScale = 0.9f,
                    maxScale = 1.6f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "JungleBush",
                    minHeight = 0.1f,
                    maxHeight = 0.5f,
                    minSlope = 0f,
                    maxSlope = 0.4f,
                    density = 0.7f,
                    minScale = 0.7f,
                    maxScale = 1.2f,
                    randomRotation = true
                },
                new VegetationData
                {
                    prefabName = "JunglePlant",
                    minHeight = 0.1f,
                    maxHeight = 0.4f,
                    minSlope = 0f,
                    maxSlope = 0.5f,
                    density = 0.8f,
                    minScale = 0.6f,
                    maxScale = 1.3f,
                    randomRotation = true
                }
            };
        }
        #endregion

        /// <summary>
        /// Сравнивает текущий объект с другим BiomeData
        /// </summary>
        /// <param name="other">Второй биом для сравнения</param>
        /// <returns>true, если объекты равны, иначе false</returns>
        public bool Equals(BiomeData other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return id == other.id;
        }

        /// <summary>
        /// Переопределение метода Equals для корректного сравнения
        /// </summary>
        /// <param name="obj">Объект для сравнения</param>
        /// <returns>true, если объекты равны, иначе false</returns>
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((BiomeData)obj);
        }

        /// <summary>
        /// Переопределение метода GetHashCode для корректного сравнения
        /// </summary>
        /// <returns>Хеш-код объекта</returns>
        public override int GetHashCode()
        {
            return id.GetHashCode();
        }
    }

    /// <summary>
    /// Данные для слоя текстуры ландшафта
    /// </summary>
    [System.Serializable]
    public class TerrainTextureLayer
    {
        [Tooltip("Начальная высота применения текстуры (0-1)")]
        [Range(0f, 1f)]
        public float heightStart = 0f;

        [Tooltip("Конечная высота применения текстуры (0-1)")]
        [Range(0f, 1f)]
        public float heightEnd = 1f;

        [Tooltip("Начальный наклон применения текстуры (0-1)")]
        [Range(0f, 1f)]
        public float slopeStart = 0f;

        [Tooltip("Конечный наклон применения текстуры (0-1)")]
        [Range(0f, 1f)]
        public float slopeEnd = 1f;

        [Tooltip("Название текстуры")]
        public string textureName = "Default";

        [Tooltip("Ссылка на текстуру")]
        public Texture2D texture;

        [Tooltip("Ссылка на карту нормалей")]
        public Texture2D normalMap;

        [Tooltip("Масштаб текстуры")]
        [Range(0.1f, 10f)]
        public float tiling = 1f;

        [Tooltip("Интенсивность нормалей")]
        [Range(0f, 2f)]
        public float normalStrength = 1f;

        /// <summary>
        /// Создает копию слоя текстуры
        /// </summary>
        /// <returns>Копия текущего слоя текстуры</returns>
        public TerrainTextureLayer Clone()
        {
            return new TerrainTextureLayer
            {
                heightStart = heightStart,
                heightEnd = heightEnd,
                slopeStart = slopeStart,
                slopeEnd = slopeEnd,
                textureName = textureName,
                texture = texture,
                normalMap = normalMap,
                tiling = tiling,
                normalStrength = normalStrength
            };
        }
    }

    /// <summary>
    /// Данные для растительности биома
    /// </summary>
    [System.Serializable]
    public class VegetationData
    {
        [Tooltip("Название префаба растительности")]
        public string prefabName = "Default";

        [Tooltip("Ссылка на префаб растительности")]
        public GameObject prefab;

        [Tooltip("Минимальная высота для размещения (0-1)")]
        [Range(0f, 1f)]
        public float minHeight = 0f;

        [Tooltip("Максимальная высота для размещения (0-1)")]
        [Range(0f, 1f)]
        public float maxHeight = 1f;

        [Tooltip("Минимальный наклон для размещения (0-1)")]
        [Range(0f, 1f)]
        public float minSlope = 0f;

        [Tooltip("Максимальный наклон для размещения (0-1)")]
        [Range(0f, 1f)]
        public float maxSlope = 0.5f;

        [Tooltip("Плотность растительности (0-1)")]
        [Range(0f, 1f)]
        public float density = 0.5f;

        [Tooltip("Минимальный масштаб")]
        public float minScale = 0.8f;

        [Tooltip("Максимальный масштаб")]
        public float maxScale = 1.2f;

        [Tooltip("Случайный поворот вокруг оси Y")]
        public bool randomRotation = true;

        [Tooltip("Выравнивать по нормали поверхности")]
        public bool alignToNormal = false;

        [Tooltip("Смещение по высоте")]
        public float heightOffset = 0f;

        [Tooltip("Группа слоев растительности")]
        public int layer = 0;

        /// <summary>
        /// Создает копию данных о растительности
        /// </summary>
        /// <returns>Копия текущих данных о растительности</returns>
        public VegetationData Clone()
        {
            return new VegetationData
            {
                prefabName = prefabName,
                prefab = prefab,
                minHeight = minHeight,
                maxHeight = maxHeight,
                minSlope = minSlope,
                maxSlope = maxSlope,
                density = density,
                minScale = minScale,
                maxScale = maxScale,
                randomRotation = randomRotation,
                alignToNormal = alignToNormal,
                heightOffset = heightOffset,
                layer = layer
            };
        }
    }
}