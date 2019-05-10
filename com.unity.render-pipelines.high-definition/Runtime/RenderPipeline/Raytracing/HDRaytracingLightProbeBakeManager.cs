using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
#if ENABLE_RAYTRACING

    public static class HDRaytracingLightProbeBakeManager
    {
        public static event System.Action<HDCamera, CommandBuffer> preRenderLightProbes;
        public static void PreRender(HDCamera camera, CommandBuffer cmdBuffer)
        {
            preRenderLightProbes?.Invoke(camera, cmdBuffer);
        }

        public static event System.Action<HDCamera, CommandBuffer, SkyManager> bakeLightProbes;
        public static void Bake(HDCamera camera, CommandBuffer cmdBuffer, SkyManager skyManager)
        {
            bakeLightProbes?.Invoke(camera, cmdBuffer, skyManager);
        }
    }
#endif
}
