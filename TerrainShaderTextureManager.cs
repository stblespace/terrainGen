using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Вспомогательный класс для управления текстурами террейна
/// без необходимости настройки Read/Write Enabled
/// </summary>
public class TerrainShaderTextureManager
{
    // Константа для максимального количества текстур
    public const int MAX_TEXTURES = 32;

    // Кеш текстур для повторного использования
    private static Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();

    /// <summary>
    /// Создаёт и инициализирует текстурный массив из списка текстур
    /// </summary>
    public static Texture2DArray CreateTextureArray(List<TerrainGenerator.Layer> textureLayers)
    {
        if (textureLayers == null || textureLayers.Count == 0)
        {
            Debug.LogWarning("No texture layers provided. Creating default texture array.");
            return CreateDefaultTextureArray();
        }

        int layerCount = textureLayers.Count;
        Debug.Log($"Creating texture array with {layerCount} layers");

        // Создаём новый массив текстур
        Texture2DArray textureArray = new Texture2DArray(512, 512, layerCount, TextureFormat.RGBA32, true, false);
        textureArray.filterMode = FilterMode.Bilinear;
        textureArray.wrapMode = TextureWrapMode.Repeat;

        // Заполняем массив текстурами
        for (int i = 0; i < layerCount; i++)
        {
            try
            {
                Texture sourceTexture = textureLayers[i].texture;

                if (sourceTexture == null)
                {
                    Debug.LogWarning($"Texture for layer {i} is null, using fallback");
                    sourceTexture = CreateColorTexture(i); // Создаем цветную текстуру для отладки
                }

                // Копируем текстуру напрямую без проверки на Readable
                CopyTextureToArray(sourceTexture, textureArray, i);
                Debug.Log($"Added texture for layer {i}: {(sourceTexture ? sourceTexture.name : "null")}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error adding texture for layer {i}: {e.Message}");

                // При ошибке создаем и используем цветную текстуру
                Texture2D fallbackTexture = CreateColorTexture(i);
                Graphics.CopyTexture(fallbackTexture, 0, 0, textureArray, i, 0);
                Object.Destroy(fallbackTexture); // Удаляем временную текстуру
            }
        }

        textureArray.Apply(false, true);
        return textureArray;
    }

    /// <summary>
    /// Копирует текстуру в текстурный массив с преобразованием размера при необходимости
    /// Не требует свойства Read/Write Enabled
    /// </summary>
    private static void CopyTextureToArray(Texture sourceTexture, Texture2DArray targetArray, int layerIndex)
    {
        // Если размер не соответствует 512x512, создаем промежуточную RenderTexture
        if (sourceTexture.width != 512 || sourceTexture.height != 512)
        {
            RenderTexture rt = RenderTexture.GetTemporary(512, 512, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;

            // Копируем исходную текстуру в RenderTexture с масштабированием
            Graphics.Blit(sourceTexture, rt);

            // Создаем временную текстуру для получения данных из RenderTexture
            Texture2D tempTexture = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            tempTexture.hideFlags = HideFlags.HideAndDontSave; // Скрываем от инспектора

            // Сохраняем активную RenderTexture
            RenderTexture prevRT = RenderTexture.active;

            // Копируем данные из RenderTexture во временную текстуру
            RenderTexture.active = rt;
            tempTexture.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
            tempTexture.Apply();

            // Восстанавливаем активную RenderTexture
            RenderTexture.active = prevRT;

            // Копируем временную текстуру в массив текстур
            Graphics.CopyTexture(tempTexture, 0, 0, targetArray, layerIndex, 0);

            // Освобождаем ресурсы
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(tempTexture);
        }
        else
        {
            // Если размер соответствует, копируем напрямую
            Graphics.CopyTexture(sourceTexture, 0, 0, targetArray, layerIndex, 0);
        }
    }

    /// <summary>
    /// Создает цветную текстуру для отладки
    /// </summary>
    private static Texture2D CreateColorTexture(int layerIndex)
    {
        Texture2D fallbackTexture = new Texture2D(512, 512, TextureFormat.RGBA32, false);

        // Выбираем цвет на основе индекса слоя
        Color layerColor;
        switch (layerIndex % 5)
        {
            case 0: layerColor = new Color(1.0f, 0.5f, 0.5f); break; // Красноватый
            case 1: layerColor = new Color(0.5f, 1.0f, 0.5f); break; // Зеленоватый
            case 2: layerColor = new Color(0.5f, 0.5f, 1.0f); break; // Синеватый
            case 3: layerColor = new Color(1.0f, 1.0f, 0.5f); break; // Желтоватый
            case 4: layerColor = new Color(1.0f, 0.5f, 1.0f); break; // Розоватый
            default: layerColor = Color.gray; break;
        }

        // Заполняем текстуру цветом с узором
        Color[] pixels = new Color[512 * 512];
        for (int y = 0; y < 512; y++)
        {
            for (int x = 0; x < 512; x++)
            {
                // Создаем узор шахматной доски
                bool isAlternate = ((x / 64) + (y / 64)) % 2 == 0;
                pixels[y * 512 + x] = isAlternate ? layerColor : layerColor * 0.8f;
            }
        }

        fallbackTexture.SetPixels(pixels);
        fallbackTexture.Apply();

        return fallbackTexture;
    }

    /// <summary>
    /// Создаёт текстурный массив по умолчанию с базовыми текстурами
    /// </summary>
    private static Texture2DArray CreateDefaultTextureArray()
    {
        Texture2DArray textureArray = new Texture2DArray(512, 512, 2, TextureFormat.RGBA32, true);

        // Создаем две цветные текстуры
        Texture2D texture1 = CreateColorTexture(0); // Красноватый
        Texture2D texture2 = CreateColorTexture(1); // Зеленоватый

        Graphics.CopyTexture(texture1, 0, 0, textureArray, 0, 0);
        Graphics.CopyTexture(texture2, 0, 0, textureArray, 1, 0);

        // Освобождаем ресурсы
        Object.Destroy(texture1);
        Object.Destroy(texture2);

        textureArray.Apply();
        return textureArray;
    }

    /// <summary>
    /// Очищает кеш текстур для освобождения памяти
    /// </summary>
    public static void ClearTextureCache()
    {
        foreach (var texture in textureCache.Values)
        {
            if (texture != null)
            {
                Object.Destroy(texture);
            }
        }

        textureCache.Clear();
        Debug.Log("Texture cache cleared");
    }
}