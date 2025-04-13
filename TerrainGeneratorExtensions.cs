using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Расширения для TerrainGenerator, обеспечивающие доступ к материалу террейна
/// </summary>
public static class TerrainGeneratorExtension
{
    // Словарь для кеширования материалов, чтобы избежать постоянного поиска
    private static Dictionary<TerrainGenerator, Material> materialCache = new Dictionary<TerrainGenerator, Material>();

    /// <summary>
    /// Получает общий материал террейна, используя материал первого чанка
    /// </summary>
    public static Material GetTerrainMaterial(this TerrainGenerator terrainGenerator)
    {
        // Проверяем кеш сначала
        if (materialCache.TryGetValue(terrainGenerator, out Material cachedMaterial))
        {
            return cachedMaterial;
        }

        // Получаем материал из первого чанка
        if (terrainGenerator.Chunks != null && terrainGenerator.Chunks.Count > 0)
        {
            var firstChunk = terrainGenerator.Chunks[0];
            if (firstChunk != null)
            {
                MeshRenderer renderer = firstChunk.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    // Кешируем материал для будущего использования
                    materialCache[terrainGenerator] = renderer.sharedMaterial;
                    return renderer.sharedMaterial;
                }
            }
        }

        // Если материал не найден, но mat доступен через отражение
        // (этот метод используем только как последнее средство)
        System.Reflection.FieldInfo matField = terrainGenerator.GetType().GetField("mat",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        if (matField != null)
        {
            Material matValue = matField.GetValue(terrainGenerator) as Material;
            if (matValue != null)
            {
                materialCache[terrainGenerator] = matValue;
                return matValue;
            }
        }

        Debug.LogWarning("Не удалось получить материал террейна. Будет создан материал по умолчанию.");
        return null;
    }

    /// <summary>
    /// Устанавливает параметр текстуры в материал террейна
    /// </summary>
    public static void SetTextureToTerrainMaterial(this TerrainGenerator terrainGenerator, string propertyName, Texture texture)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null && texture != null)
        {
            material.SetTexture(propertyName, texture);
        }
    }

    /// <summary>
    /// Устанавливает числовой параметр в материал террейна
    /// </summary>
    public static void SetFloatToTerrainMaterial(this TerrainGenerator terrainGenerator, string propertyName, float value)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null)
        {
            material.SetFloat(propertyName, value);
        }
    }

    /// <summary>
    /// Устанавливает векторный параметр в материал террейна
    /// </summary>
    public static void SetVectorToTerrainMaterial(this TerrainGenerator terrainGenerator, string propertyName, Vector4 value)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null)
        {
            material.SetVector(propertyName, value);
        }
    }

    /// <summary>
    /// Устанавливает параметр цвета в материал террейна
    /// </summary>
    public static void SetColorToTerrainMaterial(this TerrainGenerator terrainGenerator, string propertyName, Color value)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null)
        {
            material.SetColor(propertyName, value);
        }
    }

    /// <summary>
    /// Устанавливает массив значений в материал террейна
    /// </summary>
    public static void SetFloatArrayToTerrainMaterial(this TerrainGenerator terrainGenerator, string propertyName, float[] values)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null)
        {
            material.SetFloatArray(propertyName, values);
        }
    }

    /// <summary>
    /// Устанавливает целочисленный параметр в материал террейна
    /// </summary>
    public static void SetIntToTerrainMaterial(this TerrainGenerator terrainGenerator, string propertyName, int value)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null)
        {
            material.SetInt(propertyName, value);
        }
    }

    /// <summary>
    /// Включает ключевое слово в материале террейна
    /// </summary>
    public static void EnableKeywordInTerrainMaterial(this TerrainGenerator terrainGenerator, string keyword)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null)
        {
            material.EnableKeyword(keyword);
        }
    }

    /// <summary>
    /// Отключает ключевое слово в материале террейна
    /// </summary>
    public static void DisableKeywordInTerrainMaterial(this TerrainGenerator terrainGenerator, string keyword)
    {
        Material material = terrainGenerator.GetTerrainMaterial();
        if (material != null)
        {
            material.DisableKeyword(keyword);
        }
    }

    /// <summary>
    /// Очищает кеш материалов
    /// </summary>
    public static void ClearMaterialCache(this TerrainGenerator terrainGenerator)
    {
        if (materialCache.ContainsKey(terrainGenerator))
        {
            materialCache.Remove(terrainGenerator);
        }
    }
}