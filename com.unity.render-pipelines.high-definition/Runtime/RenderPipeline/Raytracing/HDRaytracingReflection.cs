using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.HDPipeline;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
#if ENABLE_RAYTRACING
    public class HDRaytracingReflections
    {
        // External structures
        HDRenderPipelineAsset m_PipelineAsset = null;
        RenderPipelineResources m_PipelineResources = null;
        SkyManager m_SkyManager = null;
        HDRaytracingManager m_RaytracingManager = null;
        SharedRTManager m_SharedRTManager = null;
        GBufferManager m_GbufferManager = null;

        // Intermediate buffer that stores the reflection pre-denoising
        RTHandleSystem.RTHandle m_LightingTexture = null;
        RTHandleSystem.RTHandle m_HitPdfTexture = null;
        RTHandleSystem.RTHandle m_VarianceBuffer = null;
        RTHandleSystem.RTHandle m_MinBoundBuffer = null;
        RTHandleSystem.RTHandle m_MaxBoundBuffer = null;

        RTHandleSystem.RTHandle m_rtReflBinTemp = null;
        RTHandleSystem.RTHandle m_rtReflBinRemap = null;

        int binningMaxBins = 32768;                  
#if true
        RenderTexture m_rtReflBinCounts { get; set; }       //counts of each mat after trace (size=binningMaxBins)
        RenderTexture m_rtReflBinOffsets { get; set; }      //offsets of each bin in binningBins (size=binningMaxBins)
        RenderTexture m_rtReflBinBlocksumCounts { get; set; }    //blocksums (size = binningMaxBins/256)
        RenderTexture m_rtReflBinBlocksumOffsets { get; set; }    //blocksums (size = binningMaxBins/256)
#else
        ComputeBuffer m_rtReflBinCounts = null;
        ComputeBuffer m_rtReflBinOffsets = null;
#endif

        // String values
        const string m_RayGenHalfResName = "RayGenHalfRes";
        const string m_RayGenIntegrationName = "RayGenIntegration";
        const string m_MissShaderName = "MissShaderReflections";

        public HDRaytracingReflections()
        {
        }

        public void Init(HDRenderPipelineAsset asset, SkyManager skyManager, HDRaytracingManager raytracingManager, SharedRTManager sharedRTManager, GBufferManager gbufferManager)
        {
            // Keep track of the pipeline asset
            m_PipelineAsset = asset;
            m_PipelineResources = asset.renderPipelineResources;

            // Keep track of the sky manager
            m_SkyManager = skyManager;

            // keep track of the ray tracing manager
            m_RaytracingManager = raytracingManager;

            // Keep track of the shared rt manager
            m_SharedRTManager = sharedRTManager;
            m_GbufferManager = gbufferManager;

            m_LightingTexture = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, useDynamicScale: true, useMipMap: false, name: "LightingBuffer");
            m_HitPdfTexture = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, useDynamicScale: true, useMipMap: false, name: "HitPdfBuffer");
            m_VarianceBuffer = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R8_UNorm, enableRandomWrite: true, useDynamicScale: true, useMipMap: false, name: "VarianceBuffer");
            m_MinBoundBuffer = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32, enableRandomWrite: true, useDynamicScale: true, useMipMap: false, name: "MinBoundBuffer");
            m_MaxBoundBuffer = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32, enableRandomWrite: true, useDynamicScale: true, useMipMap: false, name: "MaxBoundBuffer");

            m_rtReflBinTemp = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, enableRandomWrite: true, useDynamicScale: true, useMipMap: false, name: "rtReflBinTemp");
            m_rtReflBinRemap = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, enableRandomWrite: true, useDynamicScale: true, useMipMap: false, name: "rtReflBinRemap");

#if true
            if (m_rtReflBinCounts == null)
            {
                int xs = 1024;
                int ys = binningMaxBins / 1024;
                m_rtReflBinCounts = new RenderTexture(xs, ys+1, 0, GraphicsFormat.R32_UInt);
                m_rtReflBinCounts.enableRandomWrite = true;
                m_rtReflBinCounts.hideFlags = HideFlags.HideAndDontSave;
                m_rtReflBinCounts.filterMode = FilterMode.Point;
                m_rtReflBinCounts.wrapMode = TextureWrapMode.Clamp;
                m_rtReflBinCounts.anisoLevel = 0;
                m_rtReflBinCounts.name = "m_rtReflBinCounts";
                m_rtReflBinCounts.Create();
            }
            if (m_rtReflBinOffsets == null)
            {
                int xs = 1024;
                int ys = binningMaxBins / 1024;
                m_rtReflBinOffsets = new RenderTexture(xs, ys+1, 0, GraphicsFormat.R32_UInt);
                m_rtReflBinOffsets.enableRandomWrite = true;
                m_rtReflBinOffsets.hideFlags = HideFlags.HideAndDontSave;
                m_rtReflBinOffsets.filterMode = FilterMode.Point;
                m_rtReflBinOffsets.wrapMode = TextureWrapMode.Clamp;
                m_rtReflBinOffsets.anisoLevel = 0;
                m_rtReflBinOffsets.name = "m_rtReflBinOffsets";
                m_rtReflBinOffsets.Create();
            }
            if (m_rtReflBinBlocksumCounts == null)
            {
                int c = binningMaxBins / 256 >= 256 ? binningMaxBins / 256 : 256;   //figure out their max later
                int xs = 1024;
                int ys = c / 1024;
                m_rtReflBinBlocksumCounts = new RenderTexture(xs, ys+1, 0, GraphicsFormat.R32_UInt);
                m_rtReflBinBlocksumCounts.enableRandomWrite = true;
                m_rtReflBinBlocksumCounts.hideFlags = HideFlags.HideAndDontSave;
                m_rtReflBinBlocksumCounts.filterMode = FilterMode.Point;
                m_rtReflBinBlocksumCounts.wrapMode = TextureWrapMode.Clamp;
                m_rtReflBinBlocksumCounts.anisoLevel = 0;
                m_rtReflBinBlocksumCounts.name = "m_rtReflBinBlocksumCounts";
                m_rtReflBinBlocksumCounts.Create();
            }
            if (m_rtReflBinBlocksumOffsets == null)
            {
                int c = binningMaxBins / 256 >= 256 ? binningMaxBins / 256 : 256;   //figure out their max later
                int xs = 1024;
                int ys = c / 1024;
                m_rtReflBinBlocksumOffsets = new RenderTexture(xs, ys + 1, 0, GraphicsFormat.R32_UInt);
                m_rtReflBinBlocksumOffsets.enableRandomWrite = true;
                m_rtReflBinBlocksumOffsets.hideFlags = HideFlags.HideAndDontSave;
                m_rtReflBinBlocksumOffsets.filterMode = FilterMode.Point;
                m_rtReflBinBlocksumOffsets.wrapMode = TextureWrapMode.Clamp;
                m_rtReflBinBlocksumOffsets.anisoLevel = 0;
                m_rtReflBinBlocksumOffsets.name = "m_rtReflBinBlocksumOffsets";
                m_rtReflBinBlocksumOffsets.Create();
            }

#else
            if (m_rtReflBinCounts == null)
            {
                m_rtReflBinCounts = new ComputeBuffer(binningMaxBins, System.Runtime.InteropServices.Marshal.SizeOf(typeof(uint)));
            }
            if (m_rtReflBinOffsets == null)
            {
                m_rtReflBinOffsets = new ComputeBuffer(binningMaxBins, System.Runtime.InteropServices.Marshal.SizeOf(typeof(uint)));
            }
            //cmd.SetRaytracingBufferParam(shadowRaytrace, m_RayGenShadowSingleName???, HDShaderIDs._LightDatas???, m_rtReflBinCounts);

#endif

        }

        static RTHandleSystem.RTHandle ReflectionHistoryBufferAllocatorFunction(string viewName, int frameIndex, RTHandleSystem rtHandleSystem)
        {
            return rtHandleSystem.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                                        enableRandomWrite: true, useMipMap: true, autoGenerateMips: false,
                                        name: string.Format("ReflectionHistoryBuffer{0}", frameIndex));
        }

        private void DestroyObjectCustom(Object obj)
        {
#if UNITY_EDITOR
            CoreUtils.Destroy(obj);
#else
        CoreUtils.Destroy(obj);
#endif
        }

        public void Release()
        {
#if true
            if (m_rtReflBinCounts != null)
            {
                DestroyObjectCustom(m_rtReflBinCounts);
                m_rtReflBinCounts = null;
            }
            if (m_rtReflBinOffsets != null)
            {
                DestroyObjectCustom(m_rtReflBinOffsets);
                m_rtReflBinOffsets = null;
            }
            if (m_rtReflBinBlocksumCounts != null)
            {
                DestroyObjectCustom(m_rtReflBinBlocksumCounts);
                m_rtReflBinBlocksumCounts = null;
            }
            if (m_rtReflBinBlocksumOffsets != null)
            {
                DestroyObjectCustom(m_rtReflBinBlocksumOffsets);
                m_rtReflBinBlocksumOffsets = null;
            }
#else
            CoreUtils.SafeRelease(m_rtReflBinCounts);
            CoreUtils.SafeRelease(m_rtReflBinOffsets);
#endif
            RTHandles.Release(m_rtReflBinRemap);
            RTHandles.Release(m_rtReflBinTemp);

            RTHandles.Release(m_MinBoundBuffer);
            RTHandles.Release(m_MaxBoundBuffer);
            RTHandles.Release(m_VarianceBuffer);
            RTHandles.Release(m_HitPdfTexture);
            RTHandles.Release(m_LightingTexture);
        }

        public void RenderReflections(HDCamera hdCamera, CommandBuffer cmd, RTHandleSystem.RTHandle outputTexture, ScriptableRenderContext renderContext, uint frameCount)
        {
            // First thing to check is: Do we have a valid ray-tracing environment?
            HDRaytracingEnvironment rtEnvironement = m_RaytracingManager.CurrentEnvironment();
            LightLoop lightLoop = m_RaytracingManager.GetLightLoop();
            BlueNoise blueNoise = m_RaytracingManager.GetBlueNoiseManager();
            ComputeShader reflectionFilter = m_PipelineAsset.renderPipelineResources.shaders.reflectionBilateralFilterCS;
            RaytracingShader reflectionShader = m_PipelineAsset.renderPipelineResources.shaders.reflectionRaytracing;
            ComputeShader binningShaders = m_PipelineAsset.renderPipelineResources.shaders.reflectionBinningCS;
            ComputeShader scanShaders = m_PipelineAsset.renderPipelineResources.shaders.scanCS;

            bool invalidState = rtEnvironement == null || blueNoise == null
                || reflectionFilter == null || reflectionShader == null || binningShaders == null || scanShaders == null
                || m_PipelineResources.textures.owenScrambledTex == null || m_PipelineResources.textures.scramblingTex == null;

            // If no acceleration structure available, end it now
            if (invalidState)
                return;

            // Grab the acceleration structures and the light cluster to use
            RaytracingAccelerationStructure accelerationStructure = m_RaytracingManager.RequestAccelerationStructure(rtEnvironement.reflLayerMask);
            HDRaytracingLightCluster lightCluster = m_RaytracingManager.RequestLightCluster(rtEnvironement.reflLayerMask);

            // Compute the actual resolution that is needed base on the quality
            string targetRayGen = "";
            switch (rtEnvironement.reflQualityMode)
            {
                case HDRaytracingEnvironment.ReflectionsQuality.QuarterRes:
                {
                    targetRayGen = m_RayGenHalfResName;
                };
                break;
                case HDRaytracingEnvironment.ReflectionsQuality.Integration:
                {
                    targetRayGen = m_RayGenIntegrationName;
                };
                break;
            }

            // Compute the actual resolution that is needed base on the quality
            uint widthResolution = 1, heightResolution = 1;
            switch (rtEnvironement.reflQualityMode)
            {
                case HDRaytracingEnvironment.ReflectionsQuality.QuarterRes:
                    {
                        widthResolution = (uint)hdCamera.actualWidth / 2;
                        heightResolution = (uint)hdCamera.actualHeight / 2;
                    };
                    break;
                case HDRaytracingEnvironment.ReflectionsQuality.Integration:
                    {
                        widthResolution = (uint)hdCamera.actualWidth;
                        heightResolution = (uint)hdCamera.actualHeight;
                    };
                    break;
            }

            using (new ProfilingSample(cmd, "REFLECTION BINNING"))
            {
                using (new ProfilingSample(cmd, "clear bins"))
                {
                    //clear binning counts
                    int ki = scanShaders.FindKernel("ScanInit");
                    if (ki < 0)
                        return;

                    // Inputs
                    int tx = binningMaxBins;
                    int ty = 1;
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanCounts", m_rtReflBinCounts);
                    cmd.SetComputeIntParam(scanShaders, "_ScanMax", binningMaxBins);

                    uint bgroupSizeX, bgroupSizeY, bgroupSizeZ;
                    scanShaders.GetKernelThreadGroupSizes(ki, out bgroupSizeX, out bgroupSizeY, out bgroupSizeZ);

                    int tgx = (int)((tx + bgroupSizeX - 1) / bgroupSizeX);
                    int tgy = (int)((ty + bgroupSizeY - 1) / bgroupSizeY);

                    // Dispatch
                    cmd.DispatchCompute(scanShaders, ki, tgx, tgy, 1);
                }

                using (new ProfilingSample(cmd, "prepass"))
                {
                    // Fetch the right filter to use
                    int currentKernel = binningShaders.FindKernel("Prepass");

                    cmd.SetComputeTextureParam(binningShaders, currentKernel, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                    cmd.SetComputeTextureParam(binningShaders, currentKernel, "_NoiseTexture", blueNoise.textureArray16RGB);
                    cmd.SetComputeTextureParam(binningShaders, currentKernel, HDShaderIDs._ScramblingTexture, m_PipelineResources.textures.scramblingTex);
                    cmd.SetComputeIntParam(binningShaders, HDShaderIDs._SpatialFilterRadius, rtEnvironement.reflSpatialFilterRadius);
                    cmd.SetComputeFloatParam(binningShaders, HDShaderIDs._RaytracingReflectionMinSmoothness, rtEnvironement.reflMinSmoothness);

                    cmd.SetComputeIntParam(binningShaders, "_RTReflBinMax", binningMaxBins);
                    cmd.SetComputeIntParam(binningShaders, "_RTReflWidth", (int)widthResolution);
                    cmd.SetComputeIntParam(binningShaders, "_RTReflHeight", (int)heightResolution);

                    // outputs
                    cmd.SetComputeTextureParam(binningShaders, currentKernel, "_RTReflBinTemp", m_rtReflBinTemp);
                    cmd.SetComputeTextureParam(binningShaders, currentKernel, "_RTReflBinCounts", m_rtReflBinCounts);

                    // Bind the right texture for clear coat support
                    RenderTargetIdentifier clearCoatMaskTexture2 = hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred ? m_GbufferManager.GetBuffersRTI()[2] : Texture2D.blackTexture;
                    cmd.SetComputeTextureParam(binningShaders, currentKernel, HDShaderIDs._SsrClearCoatMaskTexture, clearCoatMaskTexture2);

                    uint bgroupSizeX, bgroupSizeY, bgroupSizeZ;
                    binningShaders.GetKernelThreadGroupSizes(currentKernel, out bgroupSizeX, out bgroupSizeY, out bgroupSizeZ);

                    int tgx = (int)((widthResolution + bgroupSizeX - 1) / bgroupSizeX);
                    int tgy = (int)((heightResolution + bgroupSizeY - 1) / bgroupSizeY);

                    // Compute the texture
                    cmd.DispatchCompute(binningShaders, currentKernel, tgx, tgy, 1);
                }

                using (new ProfilingSample(cmd, "scan counts->offsets"))
                {
                    int ki = scanShaders.FindKernel("ScanMain");
                    if (ki < 0)
                        return;

                    // Inputs
                    int tx = binningMaxBins / 2;    //each thread processes two bins
                    int ty = 1;
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanCounts", m_rtReflBinCounts);
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanOffsets", m_rtReflBinOffsets);
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanBlocksums", m_rtReflBinBlocksumCounts);
                    cmd.SetComputeIntParam(scanShaders, "_ScanStoreBlocksums", 1);
                    cmd.SetComputeIntParam(scanShaders, "_ScanMax", binningMaxBins); 

                    uint bgroupSizeX, bgroupSizeY, bgroupSizeZ;
                    scanShaders.GetKernelThreadGroupSizes(ki, out bgroupSizeX, out bgroupSizeY, out bgroupSizeZ);

                    int tgx = (int)((tx + bgroupSizeX - 1) / bgroupSizeX);
                    int tgy = (int)((ty + bgroupSizeY - 1) / bgroupSizeY);

                    // Dispatch
                    cmd.DispatchCompute(scanShaders, ki, tgx, tgy, 1);

                    //--------------------------------------
                    // Inputs
                    tx = 128;// 2048/256/2;binningMaxBins
                    ty = 1;
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanCounts", m_rtReflBinBlocksumCounts);   
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanOffsets", m_rtReflBinBlocksumOffsets);     
                    cmd.SetComputeIntParam(scanShaders, "_ScanStoreBlocksums", 0);
                    cmd.SetComputeIntParam(scanShaders, "_ScanMax", binningMaxBins / 256);

                    scanShaders.GetKernelThreadGroupSizes(ki, out bgroupSizeX, out bgroupSizeY, out bgroupSizeZ);

                    tgx = (int)((tx + bgroupSizeX - 1) / bgroupSizeX);
                    tgy = (int)((ty + bgroupSizeY - 1) / bgroupSizeY);

                    cmd.DispatchCompute(scanShaders, ki, tgx, tgy, 1);

                    //------------------------------------
                    ki = scanShaders.FindKernel("ScanAddBlocksums");
                    if (ki < 0)
                        return;

                    tx = binningMaxBins;
                    ty = 1;
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanBlocksums", m_rtReflBinBlocksumOffsets);
                    cmd.SetComputeTextureParam(scanShaders, ki, "_ScanOffsets", m_rtReflBinOffsets);
                    cmd.SetComputeIntParam(scanShaders, "_ScanMax", binningMaxBins);

                    scanShaders.GetKernelThreadGroupSizes(ki, out bgroupSizeX, out bgroupSizeY, out bgroupSizeZ);

                    tgx = (int)((tx + bgroupSizeX - 1) / bgroupSizeX);
                    tgy = (int)((ty + bgroupSizeY - 1) / bgroupSizeY);

                    cmd.DispatchCompute(scanShaders, ki, tgx, tgy, 1);
                }

                using (new ProfilingSample(cmd, "calcbins"))
                {
                    // Fetch the right filter to use
                    int ki = binningShaders.FindKernel("CalculateBins");

                    cmd.SetComputeIntParam(binningShaders, "_RTReflWidth", (int)widthResolution);
                    cmd.SetComputeIntParam(binningShaders, "_RTReflHeight", (int)heightResolution);

                    cmd.SetComputeTextureParam(binningShaders, ki, "_RTReflBinTemp", m_rtReflBinTemp);
                    cmd.SetComputeTextureParam(binningShaders, ki, "_RTReflBinRemap", m_rtReflBinRemap);
                    cmd.SetComputeTextureParam(binningShaders, ki, "_RTReflBinOffsets", m_rtReflBinOffsets);

                    uint bgroupSizeX, bgroupSizeY, bgroupSizeZ;
                    binningShaders.GetKernelThreadGroupSizes(ki, out bgroupSizeX, out bgroupSizeY, out bgroupSizeZ);

                    int tgx = (int)((widthResolution + bgroupSizeX - 1) / bgroupSizeX);
                    int tgy = (int)((heightResolution + bgroupSizeY - 1) / bgroupSizeY);

                    // Compute the texture
                    cmd.DispatchCompute(binningShaders, ki, tgx, tgy, 1);
                }
            }

            // Define the shader pass to use for the reflection pass
            cmd.SetRaytracingShaderPass(reflectionShader, "IndirectDXR");

            // Set the acceleration structure for the pass
            cmd.SetRaytracingAccelerationStructure(reflectionShader, HDShaderIDs._RaytracingAccelerationStructureName, accelerationStructure);

            // Inject the ray-tracing sampling data
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._OwenScrambledTexture, m_PipelineResources.textures.owenScrambledTex);
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._ScramblingTexture, m_PipelineResources.textures.scramblingTex);

            // Global reflection parameters
            cmd.SetRaytracingFloatParams(reflectionShader, HDShaderIDs._RaytracingIntensityClamp, rtEnvironement.reflClampValue);
            cmd.SetRaytracingFloatParams(reflectionShader, HDShaderIDs._RaytracingReflectionMinSmoothness, rtEnvironement.reflMinSmoothness);
            cmd.SetRaytracingFloatParams(reflectionShader, HDShaderIDs._RaytracingReflectionMaxDistance, rtEnvironement.reflBlendDistance);

            // Inject the ray generation data
            cmd.SetGlobalFloat(HDShaderIDs._RaytracingRayBias, rtEnvironement.rayBias);
            cmd.SetGlobalFloat(HDShaderIDs._RaytracingRayMaxLength, rtEnvironement.reflRayLength);
            cmd.SetRaytracingIntParams(reflectionShader, HDShaderIDs._RaytracingNumSamples, rtEnvironement.reflNumMaxSamples);
            int frameIndex = hdCamera.IsTAAEnabled() ? hdCamera.taaFrameIndex : (int)frameCount % 8;
            cmd.SetGlobalInt(HDShaderIDs._RaytracingFrameIndex, frameIndex);

            // Set the data for the ray generation
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._SsrLightingTextureRW, m_LightingTexture);
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._SsrHitPointTexture, m_HitPdfTexture);
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._NormalBufferTexture, m_SharedRTManager.GetNormalBuffer());

            //test!!!!
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, "_RTReflBinCounts", m_rtReflBinCounts);
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, "_RTReflBinOffsets", m_rtReflBinOffsets);
            //!!!
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, "_RTReflBinRemap", m_rtReflBinRemap);

            // Set ray count tex
            cmd.SetRaytracingIntParam(reflectionShader, HDShaderIDs._RayCountEnabled, m_RaytracingManager.rayCountManager.RayCountIsEnabled());
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._RayCountTexture, m_RaytracingManager.rayCountManager.rayCountTexture);

            // Compute the pixel spread value
            float pixelSpreadAngle = Mathf.Atan(2.0f * Mathf.Tan(hdCamera.camera.fieldOfView * Mathf.PI / 360.0f) / Mathf.Min(hdCamera.actualWidth, hdCamera.actualHeight));
            cmd.SetRaytracingFloatParam(reflectionShader, HDShaderIDs._RaytracingPixelSpreadAngle, pixelSpreadAngle);

            // LightLoop data
            cmd.SetGlobalBuffer(HDShaderIDs._RaytracingLightCluster, lightCluster.GetCluster());
            cmd.SetGlobalBuffer(HDShaderIDs._LightDatasRT, lightCluster.GetLightDatas());
            cmd.SetGlobalVector(HDShaderIDs._MinClusterPos, lightCluster.GetMinClusterPos());
            cmd.SetGlobalVector(HDShaderIDs._MaxClusterPos, lightCluster.GetMaxClusterPos());
            cmd.SetGlobalInt(HDShaderIDs._LightPerCellCount, rtEnvironement.maxNumLightsPercell);
            cmd.SetGlobalInt(HDShaderIDs._PunctualLightCountRT, lightCluster.GetPunctualLightCount());
            cmd.SetGlobalInt(HDShaderIDs._AreaLightCountRT, lightCluster.GetAreaLightCount());

            // Note: Just in case, we rebind the directional light data (in case they were not)
            cmd.SetGlobalBuffer(HDShaderIDs._DirectionalLightDatas, lightLoop.directionalLightDatas);
            cmd.SetGlobalInt(HDShaderIDs._DirectionalLightCount, lightLoop.m_lightList.directionalLights.Count);

            // Evaluate the clear coat mask texture based on the lit shader mode
            RenderTargetIdentifier clearCoatMaskTexture = hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred ? m_GbufferManager.GetBuffersRTI()[2] : Texture2D.blackTexture;
            cmd.SetRaytracingTextureParam(reflectionShader, targetRayGen, HDShaderIDs._SsrClearCoatMaskTexture, clearCoatMaskTexture);

            // Set the data for the ray miss
            cmd.SetRaytracingTextureParam(reflectionShader, m_MissShaderName, HDShaderIDs._SkyTexture, m_SkyManager.skyReflection);



            // Force to disable specular lighting
            cmd.SetGlobalInt(HDShaderIDs._EnableSpecularLighting, 0);

            // Run the calculus
            cmd.DispatchRays(reflectionShader, targetRayGen, widthResolution, heightResolution, 1);

            // Restore the previous state of specular lighting
            cmd.SetGlobalInt(HDShaderIDs._EnableSpecularLighting, hdCamera.frameSettings.IsEnabled(FrameSettingsField.SpecularLighting) ? 1 : 0);

            using (new ProfilingSample(cmd, "Filter Reflection", CustomSamplerId.RaytracingFilterReflection.GetSampler()))
            {
                switch (rtEnvironement.reflQualityMode)
                {
                    case HDRaytracingEnvironment.ReflectionsQuality.QuarterRes:
                    {
                        // Fetch the right filter to use
                        int currentKernel = reflectionFilter.FindKernel("RaytracingReflectionFilter");

                        // Inject all the parameters for the compute
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._SsrLightingTextureRW, m_LightingTexture);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._SsrHitPointTexture, m_HitPdfTexture);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._NormalBufferTexture, m_SharedRTManager.GetNormalBuffer());
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_NoiseTexture", blueNoise.textureArray16RGB);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_VarianceTexture", m_VarianceBuffer);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_MinColorRangeTexture", m_MinBoundBuffer);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_MaxColorRangeTexture", m_MaxBoundBuffer);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_RaytracingReflectionTexture", outputTexture);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._ScramblingTexture, m_PipelineResources.textures.scramblingTex);
                        cmd.SetComputeIntParam(reflectionFilter, HDShaderIDs._SpatialFilterRadius, rtEnvironement.reflSpatialFilterRadius);
                        cmd.SetComputeFloatParam(reflectionFilter, HDShaderIDs._RaytracingReflectionMinSmoothness, rtEnvironement.reflMinSmoothness);

                        // Texture dimensions
                        int texWidth = outputTexture.rt.width ;
                        int texHeight = outputTexture.rt.height;

                        // Evaluate the dispatch parameters
                        int areaTileSize = 8;
                        int numTilesXHR = (texWidth  + (areaTileSize - 1)) / areaTileSize;
                        int numTilesYHR = (texHeight + (areaTileSize - 1)) / areaTileSize;

                        // Bind the right texture for clear coat support
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._SsrClearCoatMaskTexture, clearCoatMaskTexture);
                        
                        // Compute the texture
                        cmd.DispatchCompute(reflectionFilter, currentKernel, numTilesXHR, numTilesYHR, 1);
                        
                        int numTilesXFR = (texWidth + (areaTileSize - 1)) / areaTileSize;
                        int numTilesYFR = (texHeight + (areaTileSize - 1)) / areaTileSize;

                        RTHandleSystem.RTHandle history = hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection)
                            ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection, ReflectionHistoryBufferAllocatorFunction, 1);
                        
                        // Fetch the right filter to use
                        currentKernel = reflectionFilter.FindKernel("TemporalAccumulationFilter");
                        cmd.SetComputeFloatParam(reflectionFilter, HDShaderIDs._TemporalAccumuationWeight, rtEnvironement.reflTemporalAccumulationWeight);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._AccumulatedFrameTexture, history);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, HDShaderIDs._CurrentFrameTexture, outputTexture);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_MinColorRangeTexture", m_MinBoundBuffer);
                        cmd.SetComputeTextureParam(reflectionFilter, currentKernel, "_MaxColorRangeTexture", m_MaxBoundBuffer);
                        cmd.DispatchCompute(reflectionFilter, currentKernel, numTilesXFR, numTilesYFR, 1);
                    }
                    break;
                    case HDRaytracingEnvironment.ReflectionsQuality.Integration:
                    {
                            switch (rtEnvironement.reflFilterMode)
                            {
                                case HDRaytracingEnvironment.ReflectionsFilterMode.SpatioTemporal:
                                {
                                    // Grab the history buffer
                                    RTHandleSystem.RTHandle reflectionHistory = hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection)
                                        ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection, ReflectionHistoryBufferAllocatorFunction, 1);

                                    // Texture dimensions
                                    int texWidth = hdCamera.actualWidth;
                                    int texHeight = hdCamera.actualHeight;

                                    // Evaluate the dispatch parameters
                                    int areaTileSize = 8;
                                    int numTilesX = (texWidth + (areaTileSize - 1)) / areaTileSize;
                                    int numTilesY = (texHeight + (areaTileSize - 1)) / areaTileSize;

                                    int m_KernelFilter = reflectionFilter.FindKernel("RaytracingReflectionTAA");

                                    // Compute the combined TAA frame
                                    var historyScale = new Vector2(hdCamera.actualWidth / (float)reflectionHistory.rt.width, hdCamera.actualHeight / (float)reflectionHistory.rt.height);
                                    cmd.SetComputeVectorParam(reflectionFilter, HDShaderIDs._ScreenToTargetScaleHistory, historyScale);
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DenoiseInputTexture, m_LightingTexture);
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DenoiseOutputTextureRW, m_HitPdfTexture);
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._ReflectionHistorybufferRW, reflectionHistory);
                                    cmd.DispatchCompute(reflectionFilter, m_KernelFilter, numTilesX, numTilesY, 1);

                                    // Output the new history
                                    HDUtils.BlitCameraTexture(cmd, hdCamera, m_HitPdfTexture, reflectionHistory);

                                    m_KernelFilter = reflectionFilter.FindKernel("ReflBilateralFilterH");

                                    // Horizontal pass of the bilateral filter
                                    cmd.SetComputeIntParam(reflectionFilter, HDShaderIDs._RaytracingDenoiseRadius, rtEnvironement.reflFilterRadius);
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DenoiseInputTexture, reflectionHistory);
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._NormalBufferTexture, m_SharedRTManager.GetNormalBuffer());
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DenoiseOutputTextureRW, m_HitPdfTexture);
                                    cmd.DispatchCompute(reflectionFilter, m_KernelFilter, numTilesX, numTilesY, 1);

                                    m_KernelFilter = reflectionFilter.FindKernel("ReflBilateralFilterV");

                                    // Horizontal pass of the bilateral filter
                                    cmd.SetComputeIntParam(reflectionFilter, HDShaderIDs._RaytracingDenoiseRadius, rtEnvironement.reflFilterRadius);
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DenoiseInputTexture, m_HitPdfTexture);
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DepthTexture, m_SharedRTManager.GetDepthStencilBuffer());
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._NormalBufferTexture, m_SharedRTManager.GetNormalBuffer());
                                    cmd.SetComputeTextureParam(reflectionFilter, m_KernelFilter, HDShaderIDs._DenoiseOutputTextureRW, outputTexture);
                                    cmd.DispatchCompute(reflectionFilter, m_KernelFilter, numTilesX, numTilesY, 1);
                                }
                                break;
                                case HDRaytracingEnvironment.ReflectionsFilterMode.None:
                                {
                                    HDUtils.BlitCameraTexture(cmd, hdCamera, m_LightingTexture, outputTexture);
                                }
                                break;
                            }
                    }
                    break;
                }
            }
        }
    }
#endif
            }
