using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class RenderVisibilityCube : ScriptableRendererFeature
{
    public enum SliceResolution : int
    {
        Tiny = 128,
        Small = 256,
        Normal = 512,
        High = 1024
    }

    public enum SlicesCount : int
    {
        Four = 4,
        Six = 6
    }

    public enum DepthBits : int
    {
        B8 = 8,
        B16 = 16,
        B24 = 24,
        B32 = 32
    }

    public enum SSProfile
    {
        RenderCube,
        ResolveCube
    }

    [Serializable]
    public class RenderVisibilityCubeSettings
    {
        public SliceResolution Resolution = SliceResolution.Normal;
        public Shader resolveSSCube;
        public Vector4 biasOffset;
        public DepthBits DepthBits = DepthBits.B16;
        public SlicesCount slicesCount = SlicesCount.Four;
        public bool cullEachSide = false;
        [Header("Blur")]
        public bool blur;
        [Range(1,4)]
        public int downsampleDivider=1;

        [Header("Obstracles Render Texture Format")]
        public bool autoDetect = true;
        public RenderTextureFormat requiredFormat;

        public int numSlices => (int)slicesCount;

        public int resolution => (int)Resolution;

        public int depthBits => (int)DepthBits;

    }

    class DrawObstraclesCubePass : ScriptableRenderPass
    {
        RenderVisibilityCubeSettings settings;
        RenderTextureDescriptor cube_atlas;
        Matrix4x4 proj_matrix;

        readonly Quaternion[] faceAngles = new Quaternion[]
        {
            Quaternion.AngleAxis(0f,Vector3.up),  //+z
            Quaternion.AngleAxis(90f,Vector3.up), //+x
            Quaternion.AngleAxis(180f,Vector3.up), //-z
            Quaternion.AngleAxis(270f,Vector3.up), //-x
            Quaternion.AngleAxis(-90f,Vector3.right), //+y
            Quaternion.AngleAxis(90f,Vector3.right) //-y
        };

        private static readonly int RTCubeTexProp = Shader.PropertyToID("_RTCubeTexProp");
        private static readonly int RTCubeTex = Shader.PropertyToID("_RTCubeTex");
        private static readonly ShaderTagId obstracleTags = new ShaderTagId("_SSObstracle");

        private static readonly int s_ViewerWorldPos = Shader.PropertyToID("_ViewerWorldPos");
        private static readonly int s_ViewerWorldOffset = Shader.PropertyToID("_ViewerWorldOffset");
        private static readonly int s_Bias = Shader.PropertyToID("_SSBias");

        private ProfilingSampler m_ProfilingSampler = ProfilingSampler.Get(SSProfile.RenderCube);

        public DrawObstraclesCubePass(RenderVisibilityCubeSettings settings)
        {
            this.settings = settings;

            cube_atlas = new RenderTextureDescriptor();
            cube_atlas.width = settings.resolution * settings.numSlices;
            cube_atlas.height = settings.resolution;
            cube_atlas.useMipMap = false;
            cube_atlas.useDynamicScale = false;
            cube_atlas.colorFormat = settings.requiredFormat;
            cube_atlas.depthBufferBits = settings.depthBits;
            cube_atlas.autoGenerateMips = false;
            cube_atlas.dimension = TextureDimension.Tex2D;
            cube_atlas.msaaSamples = 1;
            cube_atlas.mipCount = 1;

            proj_matrix = Matrix4x4.Perspective(90f, 1f, 0.001f, 100f);
        }

        static float GetFrustumFovBiasInDegrees(int shadowSliceResolution, bool shadowFiltering)
        {
            // Commented-out code below uses the theoretical formula to compute the required guard angle based on the number of additional
            // texels that the projection should cover. It is close to HDRP's HDShadowUtils.CalcGuardAnglePerspective method.
            // However, due to precision issues or other filterings performed at lighting for example, this formula also still requires a fudge factor.
            // Since we only handle a fixed number of resolutions, we use empirical values instead.
#if false
            float fudgeFactor = 1.5f;
            return fudgeFactor * CalcGuardAngle(90, shadowFiltering ? 5 : 1, shadowSliceResolution);
#endif

            float fovBias = 4.00f;

            // Empirical value found to remove gaps between point light shadow faces in test scenes.
            // We can see that the guard angle is roughly proportional to the inverse of resolution https://docs.google.com/spreadsheets/d/1QrIZJn18LxVKq2-K1XS4EFRZcZdZOJTTKKhDN8Z1b_s
            if (shadowSliceResolution <= 8)
                Debug.LogWarning("Too many additional punctual lights shadows, increase shadow atlas size or remove some shadowed lights");
            else if (shadowSliceResolution <= 16)
                fovBias = 43.0f;
            else if (shadowSliceResolution <= 32)
                fovBias = 18.55f;
            else if (shadowSliceResolution <= 64)
                fovBias = 8.63f;
            else if (shadowSliceResolution <= 128)
                fovBias = 4.13f;
            else if (shadowSliceResolution <= 256)
                fovBias = 2.03f;
            else if (shadowSliceResolution <= 512)
                fovBias = 1.00f;
            else if (shadowSliceResolution <= 1024)
                fovBias = 0.50f;

            if (shadowFiltering)
            {
                if (shadowSliceResolution <= 16)
                    Debug.LogWarning("Too many additional punctual lights shadows to use Soft Shadows. Increase shadow atlas size, remove some shadowed lights or use Hard Shadows.");
                else if (shadowSliceResolution <= 32)
                    fovBias += 9.35f;
                else if (shadowSliceResolution <= 64)
                    fovBias += 4.07f;
                else if (shadowSliceResolution <= 128)
                    fovBias += 1.77f;
                else if (shadowSliceResolution <= 256)
                    fovBias += 0.85f;
                else if (shadowSliceResolution <= 512)
                    fovBias += 0.39f;
                else if (shadowSliceResolution <= 1024)
                    fovBias += 0.17f;

                // These values were verified to work on untethered devices for which m_SupportsBoxFilterForShadows is true.
                // TODO: Investigate finer-tuned values for those platforms. Soft shadows are implemented differently for them.
            }

            return fovBias;
        }

        static float GetBias(float biasOffset, float range, int sliceResolution,bool filtering)
        {
            float frustumSize;
            // "For perspective projections, shadow texel size varies with depth
            //  It will only work well if done in receiver side in the pixel shader. Currently UniversalRP
            //  do bias on caster side in vertex shader. When we add shader quality tiers we can properly
            //  handle this. For now, as a poor approximation we do a constant bias and compute the size of
            //  the frustum as if it was orthogonal considering the size at mid point between near and far planes.
            //  Depending on how big the light range is, it will be good enough with some tweaks in bias"
            // Note: HDRP uses normalBias both in HDShadowUtils.CalcGuardAnglePerspective and HDShadowAlgorithms/EvalShadow_NormalBias (receiver bias)
            float fovBias = GetFrustumFovBiasInDegrees(sliceResolution, filtering);
            // Note: the same fovBias was also used to compute ShadowUtils.ExtractPointLightMatrix
            float cubeFaceAngle = 90 + fovBias;
            frustumSize = Mathf.Tan(cubeFaceAngle * 0.5f * Mathf.Deg2Rad) * range; // half-width (in world-space units) of shadow frustum's "far plane"

            // depth and normal bias scale is in shadowmap texel size in world space
            float texelSize = frustumSize / sliceResolution;
            float depthBias = -biasOffset * texelSize;
           // float normalBias = -shadowData.bias[shadowLightIndex].y * texelSize;

            // The current implementation of NormalBias in Universal RP is the same as in Unity Built-In RP (i.e moving shadow caster vertices along normals when projecting them to the shadow map).
            // This does not work well with Point Lights, which is why NormalBias value is hard-coded to 0.0 in Built-In RP (see value of unity_LightShadowBias.z in FrameDebugger, and native code that sets it: https://github.cds.internal.unity3d.com/unity/unity/blob/a9c916ba27984da43724ba18e70f51469e0c34f5/Runtime/Camera/Shadows.cpp#L1686 )
            // We follow the same convention in Universal RP:
           // normalBias = 0.0f;

            if (filtering)
            {
                // TODO: depth and normal bias assume sample is no more than 1 texel away from shadowmap
                // This is not true with PCF. Ideally we need to do either
                // cone base bias (based on distance to center sample)
                // or receiver place bias based on derivatives.
                // For now we scale it by the PCF kernel size of non-mobile platforms (5x5)
                const float kernelRadius = 2.5f;
                depthBias *= kernelRadius;
               // normalBias *= kernelRadius;
            }

            return depthBias;// new Vector4(depthBias,0,0,0);
        }

        private Vector3 GetTargetPos()
        {
            return ViewerPos.Position;
        }

        // This method is called before executing the render pass.
        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in a performant manner.
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            cube_atlas.width = settings.resolution * settings.numSlices;
            cube_atlas.height = settings.resolution;
            cube_atlas.depthBufferBits = settings.depthBits;
            cmd.GetTemporaryRT(RTCubeTex, cube_atlas);
            ConfigureTarget(RTCubeTex);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        private void DrawOpaqueObstraclesCulling(ScriptableRenderContext context,in Matrix4x4 view,ref RenderingData renderingData)
        {
            SortingCriteria sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
            DrawingSettings drawingSettings = CreateDrawingSettings(obstracleTags, ref renderingData, sortingCriteria);
            var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);

            if (renderingData.cameraData.camera.TryGetCullingParameters(out var cullParams))
            {
                cullParams.isOrthographic = false;
                cullParams.cullingMatrix = proj_matrix * view;
                cullParams.cullingOptions = CullingOptions.None;
                var planes = GeometryUtility.CalculateFrustumPlanes(cullParams.cullingMatrix);
                for (int i = 0; i < 6; i++)
                    cullParams.SetCullingPlane(i, planes[i]);
                var cullResults = context.Cull(ref cullParams);
                // render obstracles
                context.DrawRenderers(cullResults, ref drawingSettings, ref filteringSettings);
            }
        }

        private void DrawOpaqueObstraclesDefault(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            SortingCriteria sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
            DrawingSettings drawingSettings = CreateDrawingSettings(obstracleTags, ref renderingData, sortingCriteria);
            var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);

            // render obstracles
            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filteringSettings);
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Render Visibility Cubemap");

            using var profScope = new ProfilingScope(null, m_ProfilingSampler);

            var bias = GetBias(settings.biasOffset.x, settings.biasOffset.y, settings.resolution, false);
            var viewerpos = GetTargetPos();
            float sliceRes = settings.resolution;

            cmd.SetGlobalTexture(RTCubeTexProp, RTCubeTex);
            cmd.SetGlobalVector(s_ViewerWorldPos, viewerpos);
            cmd.SetGlobalVector(s_ViewerWorldOffset, viewerpos - renderingData.cameraData.worldSpaceCameraPos);
            cmd.SetGlobalVector(s_Bias, new Vector4(bias, settings.biasOffset.y, settings.biasOffset.z, 0));
            cmd.SetProjectionMatrix(proj_matrix);

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            for (int i = 0; i < settings.numSlices; ++i)
            {
                var view = Matrix4x4.TRS(viewerpos,faceAngles[i],new Vector3(1f, 1f, -1f));
                view = view.inverse;

                cmd.SetViewMatrix(view);
                cmd.SetViewport(new Rect(sliceRes * i, 0f, sliceRes, sliceRes));
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                if(settings.cullEachSide)
                    DrawOpaqueObstraclesCulling(context, in view, ref renderingData);
                else
                    DrawOpaqueObstraclesDefault(context, ref renderingData);
            }

            CommandBufferPool.Release(cmd);
        }

        // Cleanup any allocated resources that were created during the execution of this render pass.
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(RTCubeTex);
        }
    }

    class ResolveSSCubePass : ScriptableRenderPass,IDisposable
    {
        RenderVisibilityCubeSettings settings;
        Material resolveSSCube;

        static readonly int RTObstraclesTexProp = Shader.PropertyToID("_RTCamOnstraclesProp");
        static readonly int RTObstracles = Shader.PropertyToID("_RTObstracles");

        static readonly int tempRTPropertyId = Shader.PropertyToID("_SceneRT");
        static readonly int sceneTexParam = Shader.PropertyToID("_SceneTexParam");

        static readonly ShaderTagId obstracleTags = new ShaderTagId("_SSObstracle");

        private static readonly int s_CameraViewXExtentID = Shader.PropertyToID("_CameraViewXExtent");
        private static readonly int s_CameraViewYExtentID = Shader.PropertyToID("_CameraViewYExtent");
        private static readonly int s_CameraViewZExtentID = Shader.PropertyToID("_CameraViewZExtent");
        private static readonly int s_ProjectionParams2ID = Shader.PropertyToID("_ProjectionParams2");
        private static readonly int s_CameraViewProjectionsID = Shader.PropertyToID("_CameraViewProjections");
        private static readonly int s_CameraViewTopLeftCornerID = Shader.PropertyToID("_CameraViewTopLeftCorner");

        private static readonly int s_BlurTexture1ID = Shader.PropertyToID("_SSBlur_Texture1");
        private static readonly int s_BlurTexture2ID = Shader.PropertyToID("_SSBlur_Texture2");
       // private static readonly int s_BlurTexture3ID = Shader.PropertyToID("_SSBlur_Texture3");
       // private static readonly int s_BlurTextureFinalID = Shader.PropertyToID("_SSBlur_Texture");
        private static readonly int s_BaseMapID = Shader.PropertyToID("_BlurSrcMap");

        private static readonly int s_SrcSize = Shader.PropertyToID("_SrcSize");
        private static readonly int s_SBlurParams = Shader.PropertyToID("_BlurParams");

        private static readonly int s_Bias = Shader.PropertyToID("_SSBias");

        private const int RESOLVE_CUBE_PASS = 0,
            BLUR_HORIZONTAL_PASS = 1,
            BLUR_VERTICAL_PASS = 2,
            BLUR_FINAL_PASS = 3;

        public ResolveSSCubePass(RenderVisibilityCubeSettings settings)
        {
            this.settings = settings;
        }

        private void DrawOpaqueObstracles(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            SortingCriteria sortingCriteria = renderingData.cameraData.defaultOpaqueSortFlags;
            DrawingSettings drawingSettings = CreateDrawingSettings(obstracleTags, ref renderingData, sortingCriteria);
            var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);

            // render obstracles
            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filteringSettings);
        }

        private void SetupSSMaterial(ref RenderingData renderingData)
        {
            Matrix4x4 view = renderingData.cameraData.GetViewMatrix();
            Matrix4x4 proj = renderingData.cameraData.GetProjectionMatrix();
            var m_CameraViewProjections = proj * view;

            // camera view space without translation, used by ReconstructViewPos() to calculate view vector.
            Matrix4x4 cview = view;
            cview.SetColumn(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            Matrix4x4 cviewProj = proj * cview;
            Matrix4x4 cviewProjInv = cviewProj.inverse;

            Vector4 topLeftCorner = cviewProjInv.MultiplyPoint(new Vector4(-1, 1, -1, 1));
            Vector4 topRightCorner = cviewProjInv.MultiplyPoint(new Vector4(1, 1, -1, 1));
            Vector4 bottomLeftCorner = cviewProjInv.MultiplyPoint(new Vector4(-1, -1, -1, 1));
            Vector4 farCentre = cviewProjInv.MultiplyPoint(new Vector4(0, 0, 1, 1));
            var m_CameraTopLeftCorner = topLeftCorner;
            var m_CameraXExtent = topRightCorner - topLeftCorner;
            var m_CameraYExtent = bottomLeftCorner - topLeftCorner;
            var m_CameraZExtent = farCentre;

            CoreUtils.SetKeyword(resolveSSCube, "_SAMPLECOLOR", !settings.blur);
            CoreUtils.SetKeyword(resolveSSCube, "_SSFLOATRGBA", settings.requiredFormat != RenderTextureFormat.ARGB32);
            CoreUtils.SetKeyword(resolveSSCube, "_SIXSLICES", settings.slicesCount == SlicesCount.Six);

            resolveSSCube.SetVector(s_ProjectionParams2ID, new Vector4(1.0f / renderingData.cameraData.camera.nearClipPlane, 0.0f, 0.0f, 0.0f));
            resolveSSCube.SetMatrix(s_CameraViewProjectionsID, m_CameraViewProjections);
            resolveSSCube.SetVector(s_CameraViewTopLeftCornerID, m_CameraTopLeftCorner);
            resolveSSCube.SetVector(s_CameraViewXExtentID, m_CameraXExtent);
            resolveSSCube.SetVector(s_CameraViewYExtentID, m_CameraYExtent);
            resolveSSCube.SetVector(s_CameraViewZExtentID, m_CameraZExtent);
            resolveSSCube.SetVector(s_Bias, settings.biasOffset);
        }

        private void DrawFSQuad(CommandBuffer cmd, RenderTargetIdentifier target, int pass)
        {
            cmd.SetRenderTarget(
                target,
                RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.Store,
                target,
                RenderBufferLoadAction.DontCare,
                RenderBufferStoreAction.DontCare
            );
            cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, resolveSSCube, 0, pass);
        }

        private void DrawFSQuad(RenderTargetIdentifier src, RenderTargetIdentifier dst, CommandBuffer cmd,int pass)
        {
            cmd.SetGlobalTexture(s_BaseMapID, src);
            DrawFSQuad(cmd,dst,pass);
        }

        private void SetSourceSize(CommandBuffer cmd, RenderTextureDescriptor desc)
        {
            float width = desc.width;
            float height = desc.height;
            if (desc.useDynamicScale)
            {
                width *= ScalableBufferManager.widthScaleFactor;
                height *= ScalableBufferManager.heightScaleFactor;
            }
            cmd.SetGlobalVector(s_SrcSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.colorFormat = settings.requiredFormat;
            desc.mipCount = 1;
            desc.msaaSamples = 1;
            desc.depthBufferBits = settings.depthBits;
            cmd.GetTemporaryRT(RTObstracles, desc);
            ConfigureTarget(RTObstracles);
            ConfigureClear(ClearFlag.All, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if(resolveSSCube == null)
            {
                if (settings.resolveSSCube == null)
                {
                    settings.resolveSSCube = Shader.Find("Hidden/ResolveSSCube");
                    if (settings.resolveSSCube == null)
                        return;
                }
                resolveSSCube = new Material(settings.resolveSSCube);
            }

            CommandBuffer cmd = CommandBufferPool.Get("Resolve SS Cube");

            cmd.SetViewProjectionMatrices(renderingData.cameraData.GetViewMatrix(), renderingData.cameraData.GetProjectionMatrix());
            cmd.SetGlobalTexture(RTObstraclesTexProp, RTObstracles);
            context.ExecuteCommandBuffer(cmd);

            DrawOpaqueObstracles(context,ref renderingData);

            SetupSSMaterial(ref renderingData);

            var srcDesc = renderingData.cameraData.cameraTargetDescriptor;
            var srcColor = renderingData.cameraData.renderer.cameraColorTarget;
            cmd.Clear();
            cmd.GetTemporaryRT(tempRTPropertyId, renderingData.cameraData.cameraTargetDescriptor);
            cmd.Blit(srcColor, tempRTPropertyId);
            cmd.SetGlobalTexture(sceneTexParam, tempRTPropertyId);
            //cmd.SetGlobalTexture(sceneTexParam, srcColor);

            if (settings.blur)
            {
                RenderTextureDescriptor blur_descriptor = srcDesc;
                blur_descriptor.msaaSamples = 1;
                blur_descriptor.depthBufferBits = 0;
                blur_descriptor.width /= settings.downsampleDivider;
                blur_descriptor.height /= settings.downsampleDivider;
                blur_descriptor.colorFormat = RenderTextureFormat.ARGB32;

                RenderTargetIdentifier m_BlurTexture1Target = new RenderTargetIdentifier(s_BlurTexture1ID, 0, CubemapFace.Unknown, -1);
                RenderTargetIdentifier m_BlurTexture2Target = new RenderTargetIdentifier(s_BlurTexture2ID, 0, CubemapFace.Unknown, -1);

                cmd.SetGlobalVector(s_SBlurParams, new Vector4(1f / settings.downsampleDivider, 0, 0, 0));

                cmd.GetTemporaryRT(s_BlurTexture1ID, blur_descriptor, FilterMode.Bilinear);
                cmd.GetTemporaryRT(s_BlurTexture2ID, blur_descriptor, FilterMode.Bilinear);
                //cmd.GetTemporaryRT(s_BlurTextureFinalID, blur_descriptor, FilterMode.Bilinear);

                DrawFSQuad(cmd, s_BlurTexture1ID, RESOLVE_CUBE_PASS);

                SetSourceSize(cmd, blur_descriptor);
                DrawFSQuad(m_BlurTexture1Target, m_BlurTexture2Target, cmd, BLUR_HORIZONTAL_PASS);
                DrawFSQuad(m_BlurTexture2Target, m_BlurTexture1Target, cmd, BLUR_VERTICAL_PASS);
                DrawFSQuad(m_BlurTexture1Target, srcColor, cmd, BLUR_FINAL_PASS);

                cmd.ReleaseTemporaryRT(s_BlurTexture1ID);
                cmd.ReleaseTemporaryRT(s_BlurTexture2ID);
               // cmd.ReleaseTemporaryRT(s_BlurTextureFinalID);
            }
            else
            {
                DrawFSQuad(cmd, srcColor, RESOLVE_CUBE_PASS);
            }

            cmd.ReleaseTemporaryRT(tempRTPropertyId);

            context.ExecuteCommandBuffer(cmd);

            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(RTObstracles);
        }

        public void Dispose()
        {
            if (resolveSSCube != null)
                DestroyImmediate(resolveSSCube);
        }
    }

    public RenderVisibilityCubeSettings settings = new RenderVisibilityCubeSettings();
    DrawObstraclesCubePass m_ScriptablePass;
    ResolveSSCubePass m_ResolveSSPass;

    static RenderTextureFormat GetSupportedRTObstraclesFormat()
    {
        if(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf))
            return RenderTextureFormat.RGHalf;
        if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            return RenderTextureFormat.ARGBHalf;
        return RenderTextureFormat.ARGB32;
    }

    /// <inheritdoc/>
    public override void Create()
    {
        if(settings.autoDetect)
            settings.requiredFormat = GetSupportedRTObstraclesFormat();
        if (settings.requiredFormat != RenderTextureFormat.ARGB32)
            Shader.EnableKeyword("_SSFLOATRGBA");
        else
            Shader.DisableKeyword("_SSFLOATRGBA");

        m_ScriptablePass = new DrawObstraclesCubePass(settings);
        m_ResolveSSPass = new ResolveSSCubePass(settings);

        // Configures where the render pass should be injected.
        m_ScriptablePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        m_ResolveSSPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        m_ResolveSSPass.Dispose();
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
        renderer.EnqueuePass(m_ResolveSSPass);
    }
}


