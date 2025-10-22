using System.Runtime.InteropServices;
using UnityEngine;

//zMul小于1clumer块往前在滑，farPlane有的时候没法被完全覆盖这是不对的
//刷新代码或者参数必须开关相机，再开关脚本，太麻烦了
//!相机不在0，0，0整个计算都有问题:AABB因为要用2个Vector4表示1个cluster所以在视空间计算，那我只需要改debug重新转个空间，就能把可视化修复。

struct My_CD_DIM
{
    public float fieldOfViewY;
    public float zNear;
    public float zFar;

    public float sD;
    public float logDimY;
    public float logDepth;

    public int clusterDimX;
    public int clusterDimY;
    public int clusterDimZ;
    public int clusterDimXYZ;
};

struct AABB { public Vector4 Min; public Vector4 Max; }

[ExecuteInEditMode]
#if UNITY_5_4_OR_NEWER
[ImageEffectAllowedInSceneView]
#endif
public class ClusterBasedLighting : MonoBehaviour
{
    public Camera _camera;
    private RenderTexture _rtColor;
    private RenderTexture _rtDepth;
    private My_CD_DIM m_DimData;
    private ComputeBuffer cb_ClusterAABBs;
    [SerializeField] private ComputeShader cs_ComputeClusterAABB;
    public Material mtlDebugCluster;
    [SerializeField, Range(8, 1024)]  int tileSizeX = 32;
    [SerializeField, Range(8, 1024)]  int tileSizeY = 32;
    [SerializeField, Range(0.3f, 4f)] float zMul = 1.0f;   // ★ Z 密度缩放（0.5更粗、2更细）
    [SerializeField, Range(1, 128)] int zCap = 64;     // ★ 上限兜底

    void Start()
    {
        _rtColor = new RenderTexture(Screen.width, Screen.height, 24);
        _rtDepth = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);
    }

    void OnEnable()
    {
        if (_camera == null) _camera = GetComponent<Camera>();
        if (_camera == null) _camera = Camera.main;

        EnsureTargets(Screen.width, Screen.height);
        RecalcDimsAndBuffers();               // ← 计算维度并创建/重建 buffer
    }

    void OnDisable()
    {
        ReleaseAll();
    }

    // 只看第 0 个 AABB
    void ReadbackFirstAABB()
    {
        if (cb_ClusterAABBs == null) return;

        var arr = new AABB[1];
        cb_ClusterAABBs.GetData(arr, 0, 0, 1); // dstArray, dstOffset, srcOffset, count

        var a = arr[0];
        Debug.Log($"AABB[0] ViewSpace  Min={a.Min}  Max={a.Max}  " +
                  $"Size={a.Max - a.Min}");
    }

    void ReleaseAll()
    {
        cb_ClusterAABBs?.Dispose(); cb_ClusterAABBs = null;
        if (_rtColor) { _rtColor.Release(); _rtColor = null; }
        if (_rtDepth) { _rtDepth.Release(); _rtDepth = null; }
    }
    void RecalcDimsAndBuffers()
    {
        CalculateMDim(_camera);

        // （重）建 AABB buffer
        int stride = Marshal.SizeOf(typeof(AABB));
        int count = Mathf.Max(1, m_DimData.clusterDimXYZ);
        if (cb_ClusterAABBs == null || cb_ClusterAABBs.count != count)
        {
            cb_ClusterAABBs?.Dispose();
            cb_ClusterAABBs = new ComputeBuffer(count, stride);
        }
    }

    void EnsureTargets(int w, int h)
    {
        if (_rtColor == null || _rtColor.width != w || _rtColor.height != h)
        {
            if (_rtColor) _rtColor.Release();
            _rtColor = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { enableRandomWrite = false };
            _rtColor.Create();
        }
        if (_rtDepth == null || _rtDepth.width != w || _rtDepth.height != h)
        {
            if (_rtDepth) _rtDepth.Release();
            _rtDepth = new RenderTexture(w, h, 24, RenderTextureFormat.Depth);
            _rtDepth.Create();
        }
    }

    void OnRenderImage(RenderTexture sourceTexture, RenderTexture destTexture)
    {
        EnsureTargets(sourceTexture.width, sourceTexture.height);
        Graphics.SetRenderTarget(_rtColor.colorBuffer, _rtDepth.depthBuffer);
        GL.Clear(true, true, Color.black);
        // 先计算
        Pass_ComputeClusterAABB();
        //ReadbackFirstAABB();
        Pass_DebugCluster();

        Graphics.Blit(_rtColor, destTexture);
    }

    void UpdateClusterCBuffer(ComputeShader cs)
    {
        int[] gridDims = { m_DimData.clusterDimX, m_DimData.clusterDimY, m_DimData.clusterDimZ };
        int[] sizes = { tileSizeX, tileSizeY };
        Vector4 screenDim = new Vector4((float)Screen.width, (float)Screen.height, 1.0f / Screen.width, 1.0f / Screen.height);
        float viewNear = m_DimData.zNear;

        // 传入：ClusterCB_ViewNear, ClusterCB_ViewFar, ClusterCB_GridDim.z
        float Kz = Mathf.Exp(Mathf.Log(m_DimData.zFar / m_DimData.zNear) / m_DimData.clusterDimZ);
        cs.SetFloat("ClusterCB_NearKZ", Kz);      // ★ 新增
        cs.SetFloat("ClusterCB_ViewFar", m_DimData.zFar);   // 可选，仅做调试/验证

        cs.SetInts("ClusterCB_GridDim", gridDims);
        cs.SetFloat("ClusterCB_ViewNear", viewNear);
        cs.SetInts("ClusterCB_Size", sizes);
        cs.SetFloat("ClusterCB_NearK", 1.0f + m_DimData.sD);
        cs.SetFloat("ClusterCB_LogGridDimY", m_DimData.logDimY);
        cs.SetVector("ClusterCB_ScreenDimensions", screenDim);
    }

    void Pass_DebugCluster()
    {
        if (mtlDebugCluster == null) { Debug.LogError("mtlDebugCluster 未赋值"); return; }
        if (cb_ClusterAABBs == null) { Debug.LogError("cb_ClusterAABBs 未创建"); return; }


        GL.wireframe = true;
        // ★ 关键：把“产生AABB的相机”的 View->World 矩阵传给 GS
        mtlDebugCluster.SetMatrix("_AABBViewToWorld", _camera.cameraToWorldMatrix);
        mtlDebugCluster.SetBuffer("ClusterAABBs", cb_ClusterAABBs);
        mtlDebugCluster.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Points, m_DimData.clusterDimXYZ);

        GL.wireframe = false;
    }


    void Pass_ComputeClusterAABB()
    {
        var projectionMatrix = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, false);
        var projectionMatrixInvers = projectionMatrix.inverse;
        cs_ComputeClusterAABB.SetMatrix("_InverseProjectionMatrix", projectionMatrixInvers);

        UpdateClusterCBuffer(cs_ComputeClusterAABB);

        int threadGroups = Mathf.CeilToInt(m_DimData.clusterDimXYZ / 1024.0f);

        int kernel = cs_ComputeClusterAABB.FindKernel("CSMain");
        cs_ComputeClusterAABB.SetBuffer(kernel, "RWClusterAABBs", cb_ClusterAABBs);
        if (threadGroups < 1) threadGroups = 1;             // 兜底，避免传 0
        cs_ComputeClusterAABB.Dispatch(kernel, threadGroups, 1, 1);
    }

    void CalculateMDim(Camera cam)
    {
        // The half-angle of the field of view in the Y-direction.
        float fieldOfViewY = cam.fieldOfView * Mathf.Deg2Rad * 0.5f;//Degree 2 Radiance:  Param.CameraInfo.Property.Perspective.fFovAngleY * 0.5f;
        float zNear = cam.nearClipPlane;// Param.CameraInfo.Property.Perspective.fMinVisibleDistance;
        float zFar = cam.farClipPlane;// Param.CameraInfo.Property.Perspective.fMaxVisibleDistance;

        // Number of clusters in the screen X direction.
        int clusterDimX = Mathf.CeilToInt(Screen.width / (float)tileSizeX);
        // Number of clusters in the screen Y direction.
        int clusterDimY = Mathf.CeilToInt(Screen.height / (float)tileSizeY);

        // The depth of the cluster grid during clustered rendering is dependent on the 
        // number of clusters subdivisions in the screen Y direction.
        // Source: Clustered Deferred and Forward Shading (2012) (Ola Olsson, Markus Billeter, Ulf Assarsson).
        float sD = 2.0f * Mathf.Tan(fieldOfViewY) / (float)clusterDimY;
        float logDimY = 1.0f / Mathf.Log(1.0f + sD);

        float logDepth = Mathf.Log(zFar / zNear);
        int baseZ = Mathf.Max(1, Mathf.FloorToInt(logDepth * logDimY));
        Debug.Log("baseZ :" + baseZ);
        // ★ 只缩放“总层数”，不改变分层形状
        int clusterDimZ = Mathf.Clamp(Mathf.RoundToInt(baseZ * zMul), 1, zCap);

        m_DimData.zNear = zNear;
        m_DimData.zFar = zFar;
        m_DimData.sD = sD;
        m_DimData.fieldOfViewY = fieldOfViewY;
        m_DimData.logDepth = logDepth;
        m_DimData.logDimY = logDimY;
        m_DimData.clusterDimX = clusterDimX;
        m_DimData.clusterDimY = clusterDimY;
        m_DimData.clusterDimZ = clusterDimZ;
        m_DimData.clusterDimXYZ = clusterDimX * clusterDimY * clusterDimZ;
    }
}
