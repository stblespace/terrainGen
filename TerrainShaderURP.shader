Shader "Custom/TerrainShaderURP"
{
    Properties
    {
        // Настройки высоты террейна
        [Header(Terrain Height Settings)]
        minTerrainHeight ("Min Height", Float) = 0
        maxTerrainHeight ("Max Height", Float) = 100
        
        // Настройки текстурирования
        [Header(Terrain Texture Settings)]
        _textureScale ("Texture Scale", Float) = 1.0
        [NoScaleOffset] terrainTextures ("Terrain Texture Array", 2DArray) = "white" {}
        
        // Настройки LOD
        [Header(LOD Settings)]
        _LODLevel ("LOD Level", Range(0, 1)) = 0
        _DistanceFromCamera ("Distance From Camera", Float) = 0
        _FadeStartDistance ("Fade Start Distance", Float) = 300
        _FadeEndDistance ("Fade End Distance", Float) = 500
        _FogColor ("Fog Color", Color) = (0.5, 0.5, 0.5, 1)
        _FogEnabled ("Fog Enabled", Float) = 1
        
        // Настройки швов между чанками
        [Header(Chunk Seam Settings)]
        _SeamFix ("Fix Chunk Seams", Float) = 1
        _ChunkSize ("Chunk Size", Float) = 16
        _SeamWidth ("Seam Width", Range(0.01, 1.0)) = 0.1
        _SeamSmoothingStrength ("Seam Smoothing Strength", Range(0.0, 1.0)) = 0.5
    }
    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 100
        
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define MAX_TEXTURES 32
            
            // Свойства шейдера
            float _textureScale;
            float minTerrainHeight;
            float maxTerrainHeight;
            float terrainHeights[MAX_TEXTURES];
            float4 _ChunkPosition;
            float _LODLevel;
            float _DistanceFromCamera;
            float _FadeStartDistance;
            float _FadeEndDistance;
            float4 _FogColor;
            float _FogEnabled;
            float _SeamFix;
            float _ChunkSize;
            float _SeamWidth;
            float _SeamSmoothingStrength;
            
            TEXTURE2D_ARRAY(terrainTextures);
            SAMPLER(sampler_terrainTextures);
            int numTextures;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 color : COLOR;
                float fogFactor : TEXCOORD3;
                float distanceFromCamera : TEXCOORD4;
                float3 localPosition : TEXCOORD5;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                
                // Стандартные преобразования вершин и нормалей
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.color = input.color;
                
                // Сохраняем локальную позицию для обнаружения границ чанков
                output.localPosition = input.positionOS.xyz;
                
                // Calculate fog factor
                output.fogFactor = ComputeFogFactor(output.positionHCS.z);
                output.distanceFromCamera = _DistanceFromCamera;
                
                return output;
            }

            // Определение, находится ли пиксель вблизи границы чанка
            float IsNearChunkEdge(float3 localPos, float chunkSize, float edgeWidth)
            {
                // Расстояние до ближайшей границы по X
                float distToEdgeX = min(
                    localPos.x - 0, // расстояние до левой границы
                    chunkSize - localPos.x // расстояние до правой границы
                );
                
                // Расстояние до ближайшей границы по Z
                float distToEdgeZ = min(
                    localPos.z - 0, // расстояние до нижней границы
                    chunkSize - localPos.z // расстояние до верхней границы
                );
                
                // Минимальное расстояние до любой границы
                float minDist = min(distToEdgeX, distToEdgeZ);
                
                // Нормализуем значение в диапазоне [0..1],
                // где 0 означает "на границе", а 1 означает "далеко от границы"
                float edgeFactor = saturate(minDist / edgeWidth);
                
                // Инвертируем значение: 1 на границе, 0 вдали от границы
                return 1.0 - edgeFactor;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 globalPos = input.positionWS;
                float2 scaledUV = globalPos.xz / _textureScale;
                
                // Вычисляем нормализованную высоту (0..1) для выбора текстуры
                float heightValue = saturate((globalPos.y - minTerrainHeight) / (maxTerrainHeight - minTerrainHeight));

                int layerIndex1 = 0;
                int layerIndex2 = 0;
                float blendFactor = 0.0;

                // Находим две текстуры между которыми будем интерполировать
                // Start with the first texture as default
                for (int i = 0; i < numTextures - 1; i++)
                {
                    if (heightValue >= terrainHeights[i] && heightValue <= terrainHeights[i + 1])
                    {
                        layerIndex1 = i;
                        layerIndex2 = i + 1;

                        // Calculate blend factor between the two textures
                        float range = terrainHeights[i + 1] - terrainHeights[i];
                        if (range > 0.001f) // Prevent division by zero
                        {
                            blendFactor = (heightValue - terrainHeights[i]) / range;
                        }
                        break;
                    }
                }

                // Если высота выше верхней границы
                if (heightValue >= terrainHeights[numTextures - 1])
                {
                    layerIndex1 = numTextures - 1;
                    layerIndex2 = numTextures - 1;
                    blendFactor = 0.0;
                }
                
                // Calculate mip level based on distance for LOD
                float distanceFactor = saturate(_LODLevel * 3); // Scale LOD effect
                float mipLevel = distanceFactor * 4.0; // Use up to 4 mip levels for distant terrain
                
                // Adjust UV scale based on distance for better performance
                float2 lodScaledUV = scaledUV / (1.0 + distanceFactor * 2.0);
                
                // Sample textures with appropriate LOD level
                half4 tex1, tex2;
                
                // Use appropriate sampling based on distance
                if (_LODLevel < 0.3) {
                    // Close distances: use detailed textures
                    tex1 = SAMPLE_TEXTURE2D_ARRAY_LOD(terrainTextures, sampler_terrainTextures, scaledUV, layerIndex1, mipLevel);
                    tex2 = SAMPLE_TEXTURE2D_ARRAY_LOD(terrainTextures, sampler_terrainTextures, scaledUV, layerIndex2, mipLevel);
                } else {
                    // Far distances: use simplified sampling with lower detail
                    tex1 = SAMPLE_TEXTURE2D_ARRAY_LOD(terrainTextures, sampler_terrainTextures, lodScaledUV, layerIndex1, mipLevel);
                    tex2 = SAMPLE_TEXTURE2D_ARRAY_LOD(terrainTextures, sampler_terrainTextures, lodScaledUV, layerIndex2, mipLevel);
                }
                
                // Interpolate between the two textures
                half4 finalColor = lerp(tex1, tex2, blendFactor);
                
                // ---- ОБРАБОТКА ШВОВ МЕЖДУ ЧАНКАМИ ----
                if (_SeamFix > 0.5) {
                    // Проверяем, находится ли пиксель вблизи края чанка
                    float edgeFactor = IsNearChunkEdge(input.localPosition, _ChunkSize, _SeamWidth);
                    
                    if (edgeFactor > 0.01) {
                        // Для пикселей рядом с краем, сглаживаем результат для устранения швов
                        
                        // 1. Используем более размытые мипмапы на границах
                        float edgeMipBias = edgeFactor * 1.5 * _SeamSmoothingStrength;
                        float edgeMipLevel = mipLevel + edgeMipBias;
                        
                        // Повторно сэмплируем текстуру с более высоким уровнем мипмапа
                        half4 edgeTex1 = SAMPLE_TEXTURE2D_ARRAY_LOD(terrainTextures, sampler_terrainTextures, scaledUV, layerIndex1, edgeMipLevel);
                        half4 edgeTex2 = SAMPLE_TEXTURE2D_ARRAY_LOD(terrainTextures, sampler_terrainTextures, scaledUV, layerIndex2, edgeMipLevel);
                        half4 edgeColor = lerp(edgeTex1, edgeTex2, blendFactor);
                        
                        // 2. Слегка смягчаем нормали на краях (отключаем часть деталей)
                        float normalBlendFactor = edgeFactor * _SeamSmoothingStrength;
                        
                        // Плавно смешиваем обычный цвет и сглаженный вариант
                        finalColor = lerp(finalColor, edgeColor, normalBlendFactor);
                    }
                }
                
                // Calculate basic lighting
                float3 normalWS = normalize(input.normalWS);
                float3 lightDir = _MainLightPosition.xyz;
                float ndotl = saturate(dot(normalWS, lightDir));
                float3 ambient = SampleSH(normalWS);
                
                float3 lighting = ambient + ndotl * _MainLightColor.rgb;
                
                // Apply lighting to color
                finalColor.rgb *= lighting;
                
                // Calculate distance fade to avoid white terrain at distance
                float distanceFade = 1.0;
                if (_FogEnabled > 0.5) {
                    distanceFade = saturate(1.0 - ((input.distanceFromCamera - _FadeStartDistance) / (_FadeEndDistance - _FadeStartDistance)));
                }
                
                // Apply custom distance fog 
                finalColor.rgb = lerp(_FogColor.rgb, finalColor.rgb, distanceFade);
                
                // Also apply Unity's built-in fog
                finalColor.rgb = MixFog(finalColor.rgb, input.fogFactor);
                
                return finalColor;
            }
            ENDHLSL
        }
        
        // Shadow casting pass
        Pass
        {
            Name "ShadowCaster"
            Tags {"LightMode" = "ShadowCaster"}

            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            
            // Required to compile gles 2.0 with standard srp library
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x
            #pragma target 2.0
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}