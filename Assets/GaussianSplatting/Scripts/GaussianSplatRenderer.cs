using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[BurstCompile]
public class GaussianSplatRenderer : MonoBehaviour
{
    public enum RenderMode
    {
        Splats,
        DebugPoints,
        DebugPointIndices,
        DebugBoxes,
        DebugChunkBounds,
    }

    public enum DisplayDataMode
    {
        None = 0,
        Position = 1,
        Scale = 2,
        Rotation = 3,
        Color = 4,
        Opacity = 5,
        SH1, SH2, SH3, SH4, SH5, SH6, SH7, SH8, SH9, SH10, SH11, SH12, SH13, SH14, SH15,
    }

    [Header("Data Asset")]

    public GaussianSplatAsset m_Asset;

    [Header("Render Options")]

    [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
    public float m_SplatScale = 1.0f;
    [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
    public int m_SHOrder = 3;
    [Range(1,30)] [Tooltip("Sort splats only every N frames")]
    public int m_SortNthFrame = 1;

    [Header("Debugging Tweaks")]

    public RenderMode m_RenderMode = RenderMode.Splats;
    [Range(1.0f,15.0f)]
    public float m_PointDisplaySize = 3.0f;
    public DisplayDataMode m_DisplayData = DisplayDataMode.None;

    [Tooltip("Use AMD FidelityFX sorting when available, instead of the slower bitonic sort")]
    public bool m_PreferFfxSort = true; // use AMD FidelityFX sort if available (currently: DX12, Vulkan, Metal, but *not* DX11)

    [Header("Resources")]

    public Shader m_ShaderSplats;
    public Shader m_ShaderComposite;
    public Shader m_ShaderDebugPoints;
    public Shader m_ShaderDebugBoxes;
    public Shader m_ShaderDebugData;
    [Tooltip("Gaussian splatting utilities compute shader")]
    public ComputeShader m_CSSplatUtilities;
    [Tooltip("'Island' bitonic sort compute shader")]
    [FormerlySerializedAs("m_CSGpuSort")]
    public ComputeShader m_CSIslandSort;
    [Tooltip("AMD FidelityFX sort compute shader")]
    public ComputeShader m_CSFfxSort;

    GraphicsBuffer m_GpuSortDistances;
    GraphicsBuffer m_GpuSortKeys;
    //GraphicsBuffer m_GpuChunks;

    IslandGPUSort m_SorterIsland;
    IslandGPUSort.Args m_SorterIslandArgs;
    FfxParallelSort m_SorterFfx;
    FfxParallelSort.Args m_SorterFfxArgs;

    CommandBuffer m_RenderCommandBuffer;
    readonly HashSet<Camera> m_CameraCommandBuffersDone = new();

    Material m_MatSplats;
    Material m_MatComposite;
    Material m_MatDebugPoints;
    Material m_MatDebugBoxes;
    Material m_MatDebugData;

    int m_FrameCounter;

    public GaussianSplatAsset asset => m_Asset;

    public void OnEnable()
    {
        Camera.onPreCull += OnPreCullCamera;

        m_FrameCounter = 0;
        if (m_Asset == null || m_Asset.m_SplatCount == 0)
        {
            Debug.LogWarning($"{nameof(GaussianSplatRenderer)} asset is null or empty", this);
            return;
        }
        if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_ShaderDebugData == null || m_CSSplatUtilities == null || m_CSIslandSort == null)
        {
            Debug.LogWarning($"{nameof(GaussianSplatRenderer)} shader references are not set up", this);
            return;
        }

        m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
        m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
        m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
        m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};
        m_MatDebugData = new Material(m_ShaderDebugData) {name = "GaussianDebugData"};

        //@TODO
        //m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (m_SplatCount + kChunkSize - 1) / kChunkSize, UnsafeUtility.SizeOf<ChunkData>()) { name = "GaussianChunkData" };

        int splatCountNextPot = Mathf.NextPowerOfTwo(m_Asset.m_SplatCount);
        m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4) { name = "GaussianSplatSortDistances" };
        m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4) { name = "GaussianSplatSortIndices" };

        // init keys buffer to splat indices
        m_CSSplatUtilities.SetBuffer(0, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetInt("_SplatCountPOT", m_GpuSortDistances.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(0, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(0, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        m_SorterIsland = new IslandGPUSort(m_CSIslandSort);
        m_SorterIslandArgs.keys = m_GpuSortDistances;
        m_SorterIslandArgs.values = m_GpuSortKeys;
        m_SorterIslandArgs.count = (uint)splatCountNextPot;

        m_SorterFfx = new FfxParallelSort(m_CSFfxSort);
        m_SorterFfxArgs.inputKeys = m_GpuSortDistances;
        m_SorterFfxArgs.inputValues = m_GpuSortKeys;
        m_SorterFfxArgs.count = (uint) m_Asset.m_SplatCount;
        if (m_SorterFfx.Valid)
            m_SorterFfxArgs.resources = FfxParallelSort.SupportResources.Load((uint)m_Asset.m_SplatCount);

        m_RenderCommandBuffer = new CommandBuffer {name = "GaussianRender"};
    }

    /*
    public void UpdateGPUBuffers()
    {
        NativeArray<Vector3> inputPositions = new(m_SplatCount, Allocator.Temp);
        NativeArray<ChunkData> chunks = new(m_GpuChunks.count, Allocator.Temp);
        ChunkData chunk = default;
        int chunkIndex = 0;
        for (var i = 0; i < m_SplatCount; ++i)
        {
            var pos = m_SplatData[i].pos;
            inputPositions[i] = pos;

            if (i % kChunkSize == 0)
            {
                chunk.bmin = float.PositiveInfinity;
                chunk.bmax = float.NegativeInfinity;
            }

            chunk.bmin = math.min(chunk.bmin, pos);
            chunk.bmax = math.max(chunk.bmax, pos);
            if ((i + 1) % kChunkSize == 0)
            {
                chunks[chunkIndex] = chunk;
                ++chunkIndex;
            }
        }

        if (chunkIndex < chunks.Length) // store last perhaps not full chunk
            chunks[chunkIndex] = chunk;
        m_GpuPositions.SetData(inputPositions);
        inputPositions.Dispose();

        m_GpuChunks.SetData(chunks);
        chunks.Dispose();

        m_GpuData.SetData(m_SplatData, 0, 0, m_SplatCount);
    }
    */

    void OnPreCullCamera(Camera cam)
    {
        m_RenderCommandBuffer.Clear();

        if (m_Asset == null || m_Asset.m_SplatCount == 0)
            return;

        Material displayMat = m_RenderMode switch
        {
            RenderMode.DebugPoints => m_MatDebugPoints,
            RenderMode.DebugPointIndices => m_MatDebugPoints,
            RenderMode.DebugBoxes => m_MatDebugBoxes,
            RenderMode.DebugChunkBounds => m_MatDebugBoxes,
            _ => m_MatSplats
        };
        if (displayMat == null)
            return;

        if (!m_CameraCommandBuffersDone.Contains(cam))
        {
            cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_RenderCommandBuffer);
            m_CameraCommandBuffersDone.Add(cam);
        }

        SetAssetTexturesOnMaterial(displayMat);

        //displayMat.SetBuffer("_ChunkBuffer", m_GpuChunks); //@TODO
        //displayMat.SetInteger("_ChunkCount", m_GpuChunks.count);

        displayMat.SetBuffer("_OrderBuffer", m_GpuSortKeys);
        displayMat.SetFloat("_SplatScale", m_SplatScale);
        displayMat.SetFloat("_SplatSize", m_PointDisplaySize);
        displayMat.SetInteger("_SplatCount", m_Asset.m_SplatCount);
        displayMat.SetInteger("_SHOrder", m_SHOrder);
        displayMat.SetInteger("_DisplayIndex", m_RenderMode == RenderMode.DebugPointIndices ? 1 : 0);
        bool displayAsLine = false; //m_RenderMode == RenderMode.DebugPointIndices;
        displayMat.SetInteger("_DisplayLine", displayAsLine ? 1 : 0);
        displayMat.SetInteger("_DisplayChunks", m_RenderMode == RenderMode.DebugChunkBounds ? 1 : 0);

        if (m_FrameCounter % m_SortNthFrame == 0)
            SortPoints(cam);
        ++m_FrameCounter;

        int vertexCount = 6;
        int instanceCount = m_Asset.m_SplatCount;
        MeshTopology topology = MeshTopology.Triangles;
        if (m_RenderMode is RenderMode.DebugBoxes or RenderMode.DebugChunkBounds)
            vertexCount = 36;
        if (displayAsLine)
        {
            topology = MeshTopology.LineStrip;
            vertexCount = m_Asset.m_SplatCount;
            instanceCount = 1;
        }
        //@TODO
        //if (m_RenderMode == RenderMode.DebugChunkBounds)
        //    instanceCount = m_GpuChunks.count;

        int rtNameID = Shader.PropertyToID("_GaussianSplatRT");
        m_RenderCommandBuffer.GetTemporaryRT(rtNameID, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
        m_RenderCommandBuffer.SetRenderTarget(rtNameID, BuiltinRenderTextureType.CurrentActive);
        m_RenderCommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0,0,0,0), 0, 0);
        m_RenderCommandBuffer.DrawProcedural(Matrix4x4.identity, displayMat, 0, topology, vertexCount, instanceCount);
        m_RenderCommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        m_RenderCommandBuffer.DrawProcedural(Matrix4x4.identity, m_MatComposite, 0, MeshTopology.Triangles, 6, 1);
        m_RenderCommandBuffer.ReleaseTemporaryRT(rtNameID);


        if (m_DisplayData != DisplayDataMode.None)
        {
            SetAssetTexturesOnMaterial(m_MatDebugData);
            m_MatDebugData.SetInteger("_SplatCount", m_Asset.m_SplatCount);
            m_MatDebugData.SetInteger("_DisplayMode", (int)m_DisplayData);

            m_MatDebugData.SetVector("_BoundsMin", m_Asset.m_BoundsMin);
            m_MatDebugData.SetVector("_BoundsMax", m_Asset.m_BoundsMax);
            Graphics.DrawProcedural(m_MatDebugData, new Bounds(cam.transform.position, Vector3.one * 1000.0f), MeshTopology.Triangles, 6, 1, cam);
        }
    }

    void SetAssetTexturesOnMaterial(Material displayMat)
    {
        displayMat.SetTexture("_TexPos", m_Asset.GetTex(GaussianSplatAsset.TexType.Pos));
        displayMat.SetTexture("_TexRot", m_Asset.GetTex(GaussianSplatAsset.TexType.Rot));
        displayMat.SetTexture("_TexScl", m_Asset.GetTex(GaussianSplatAsset.TexType.Scl));
        displayMat.SetTexture("_TexCol", m_Asset.GetTex(GaussianSplatAsset.TexType.Col));
        displayMat.SetTexture("_TexSH1", m_Asset.GetTex(GaussianSplatAsset.TexType.SH1));
        displayMat.SetTexture("_TexSH2", m_Asset.GetTex(GaussianSplatAsset.TexType.SH2));
        displayMat.SetTexture("_TexSH3", m_Asset.GetTex(GaussianSplatAsset.TexType.SH3));
        displayMat.SetTexture("_TexSH4", m_Asset.GetTex(GaussianSplatAsset.TexType.SH4));
        displayMat.SetTexture("_TexSH5", m_Asset.GetTex(GaussianSplatAsset.TexType.SH5));
        displayMat.SetTexture("_TexSH6", m_Asset.GetTex(GaussianSplatAsset.TexType.SH6));
        displayMat.SetTexture("_TexSH7", m_Asset.GetTex(GaussianSplatAsset.TexType.SH7));
        displayMat.SetTexture("_TexSH8", m_Asset.GetTex(GaussianSplatAsset.TexType.SH8));
        displayMat.SetTexture("_TexSH9", m_Asset.GetTex(GaussianSplatAsset.TexType.SH9));
        displayMat.SetTexture("_TexSHA", m_Asset.GetTex(GaussianSplatAsset.TexType.SHA));
        displayMat.SetTexture("_TexSHB", m_Asset.GetTex(GaussianSplatAsset.TexType.SHB));
        displayMat.SetTexture("_TexSHC", m_Asset.GetTex(GaussianSplatAsset.TexType.SHC));
        displayMat.SetTexture("_TexSHD", m_Asset.GetTex(GaussianSplatAsset.TexType.SHD));
        displayMat.SetTexture("_TexSHE", m_Asset.GetTex(GaussianSplatAsset.TexType.SHE));
        displayMat.SetTexture("_TexSHF", m_Asset.GetTex(GaussianSplatAsset.TexType.SHF));
    }

    public void OnDisable()
    {
        Camera.onPreCull -= OnPreCullCamera;

        m_CameraCommandBuffersDone?.Clear();
        m_RenderCommandBuffer?.Clear();

        //m_GpuChunks?.Dispose(); //@TODO
        m_GpuSortDistances?.Dispose();
        m_GpuSortKeys?.Dispose();
        m_SorterFfxArgs.resources.Dispose();

        DestroyImmediate(m_MatSplats);
        DestroyImmediate(m_MatComposite);
        DestroyImmediate(m_MatDebugPoints);
        DestroyImmediate(m_MatDebugBoxes);
        DestroyImmediate(m_MatDebugData);
    }

    void SortPoints(Camera cam)
    {
        bool useFfx = m_PreferFfxSort && m_SorterFfx.Valid;
        Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
        if (useFfx)
        {
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;
        }

        // calculate distance to the camera for each splat
        m_CSSplatUtilities.SetTexture(1, "_SplatPositions", m_Asset.GetTex(GaussianSplatAsset.TexType.Pos));
        m_CSSplatUtilities.SetBuffer(1, "_SplatSortDistances", m_GpuSortDistances);
        m_CSSplatUtilities.SetBuffer(1, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetMatrix("_WorldToCameraMatrix", worldToCamMatrix);
        m_CSSplatUtilities.SetInt("_SplatCount", m_Asset.m_SplatCount);
        m_CSSplatUtilities.SetInt("_SplatCountPOT", m_GpuSortDistances.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(1, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(1, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        // sort the splats
        if (useFfx)
            m_SorterFfx.Dispatch(m_RenderCommandBuffer, m_SorterFfxArgs);
        else
            m_SorterIsland.Dispatch(m_RenderCommandBuffer, m_SorterIslandArgs);
    }

    public void ActivateCamera(int index)
    {
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            var cam = m_Asset.m_Cameras[index];
            mainCam.transform.position = cam.pos;
            mainCam.transform.LookAt(cam.pos + cam.axisZ, cam.axisY);
            #if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(mainCam.transform);
            #endif
        }
    }
}
