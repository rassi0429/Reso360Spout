using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Reso360Spout
{
    public class UnityEntry : MonoBehaviour
    {
        // --- Public Fields --------------------------------------------------
        public UnityEngine.Camera CameraComponent;
        public CubemapToOtherProjection cubeComponent;
        public IntPtr Plugin = IntPtr.Zero;
        public UnityEngine.RenderTexture SourceTexture;
        public UnityEngine.Texture2D SharedTexture;

        // --- Static Fields (Shaders) ---------------------------------------
        public static UnityEngine.Shader cubemapShader;
        public static UnityEngine.Shader cubemapRenderer;

        // --- Private Fields ------------------------------------------------
        private GameObject _root;
        private GameObject _cameraRoot;

        // --- MonoBehaviour Methods -----------------------------------------
        private void Start()
        {
            LoadShadersFromAssetBundle();

            // ルートオブジェクトの参照
            _root = gameObject;

            // カメラ用の GameObject 生成
            CreateCameraRoot();

            // CubemapToOtherProjection の設定
            cubeComponent = _cameraRoot.AddComponent<CubemapToOtherProjection>();

            // Spout（プラグイン）有効時の初期化
            if (Main.Config.GetValue<bool>(Main.SPOUT_ENABLE))
            {
                InitSpoutPlugin();
            }

            // 各種設定の反映
            cubeComponent.RenderInStereo = Main.Config.GetValue<bool>(Main.RENDER_IN_STEREO);
            cubeComponent.ProjectionType = Main.Config.GetValue<ProjectionType>(Main.PROJECTION_TYPE);
            cubeComponent.CubemapSize = (int)Main.Config.GetValue<Main.CubeMapSize>(Main.CUBEMAP_SIZE);

            CameraComponent.nearClipPlane = Main.Config.GetValue<float>(Main.NEAR_CLIP);
            CameraComponent.farClipPlane = Main.Config.GetValue<float>(Main.FAR_CLIP);

            // Ensure bit 28 is off at startup
            CameraComponent.cullingMask &= ~(1 << 28);
        }

        private void Update()
        {
            try
            {
                // カメラのトランスフォームを毎フレーム更新
                _root.transform.position = Main.CameraOrigin;
                _root.transform.rotation = Main.CameraRotation;
                _root.transform.localScale = Main.CameraScale;

                // プラグイン内部状態を更新
                if (Plugin != IntPtr.Zero)
                {
                    SpoutUtil.IssuePluginEvent(PluginEntry.Event.Update, Plugin);
                }

                // RenderTexture 送出
                if (SourceTexture != null)
                {
                    SendRenderTexture();
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Reso360Spout] " + e);
            }
        }

        // --- Private Methods -----------------------------------------------
        /// <summary>
        /// AssetBundle からシェーダーをロードして static フィールドに設定する
        /// </summary>
        private void LoadShadersFromAssetBundle()
        {
            var assets = AssetBundle.LoadFromFile(@"rml_mods\cubeto360");
            if (assets == null)
            {
                Debug.LogWarning("[Reso360Spout] AssetBundle not found at rml_mods\\cubeto360");
                return;
            }

            // 全ての Shader をロード
            var allShaders = assets.LoadAllAssets<UnityEngine.Shader>();
            foreach (var shader in allShaders)
            {
                if (shader.name == "Unlit/CubemapToOtherProjection")
                {
                    cubemapShader = shader;
                }
                else if (shader.name == "Unlit/CubemapRenderer")
                {
                    cubemapRenderer = shader;
                }
            }

            Debug.Log("[Reso360Spout] Shaders loaded.");
        }

        /// <summary>
        /// カメラ用のゲームオブジェクトを作成し、初期化する
        /// </summary>
        private void CreateCameraRoot()
        {
            _cameraRoot = new GameObject("CameraRoot");
            _cameraRoot.transform.SetParent(_root.transform, false);
            _cameraRoot.transform.localPosition = Vector3.zero;
            _cameraRoot.transform.localRotation = Quaternion.identity;

            // Camera の設定
            CameraComponent = _cameraRoot.AddComponent<UnityEngine.Camera>();
            CameraComponent.depth = -128;
            CameraComponent.fieldOfView = 90.0f;
            CameraComponent.stereoTargetEye = StereoTargetEyeMask.None;
            CameraComponent.stereoSeparation = 0.065f;

        }
        /// <summary>
        /// Spout（プラグイン）を初期化する
        /// </summary>
        private void InitSpoutPlugin()
        {
            // 出力解像度を取得
            var resolution = Main.Config.GetValue(Main.OUTPUT_SIZE);
            Plugin = PluginEntry.CreateSender("VRCam", resolution.x, resolution.y);

            // RenderTexture の作成
            SourceTexture = new UnityEngine.RenderTexture(resolution.x, resolution.y, 24);
            cubeComponent.RenderTarget = SourceTexture;
        }

        /// <summary>
        /// CommandBuffer を利用して RenderTexture の内容を SharedTexture に転送
        /// </summary>
        private void SendRenderTexture()
        {
            // Spout プラグイン未初期化時はスキップ
            if (Plugin == IntPtr.Zero)
            {
                Debug.Log("[Reso360Spout] Spout plugin not ready.");
                return;
            }

            // SharedTexture の初期化
            if (SharedTexture == null && Main.Config.GetValue(Main.SPOUT_ENABLE))
            {
                var ptr = PluginEntry.GetTexturePointer(Plugin);
                if (ptr != IntPtr.Zero)
                {
                    SharedTexture = UnityEngine.Texture2D.CreateExternalTexture(
                        PluginEntry.GetTextureWidth(Plugin),
                        PluginEntry.GetTextureHeight(Plugin),
                        TextureFormat.ARGB32,
                        false,
                        false,
                        ptr
                    );
                    SharedTexture.hideFlags = HideFlags.DontSave;
                }
            }

            // SharedTexture が存在しなければ処理を中断
            if (SharedTexture == null) return;

            // コマンドバッファ作成
            var cmd = new CommandBuffer { name = "SpoutSend" };

            // 一時的な RT の ID を確保
            int tempRTId = UnityEngine.Shader.PropertyToID("_TempSpoutRT");
            cmd.GetTemporaryRT(tempRTId,
                SharedTexture.width,
                SharedTexture.height,
                0,
                FilterMode.Bilinear,
                RenderTextureFormat.ARGB32
            );

            // scale, offset を使ってブリット
            cmd.Blit(
                SourceTexture,
                tempRTId,
                new Vector2(1.0f, -1.0f), // scale
                new Vector2(0.0f, 1.0f)   // offset
            );

            // CopyTexture
            cmd.CopyTexture(tempRTId, 0, 0, SharedTexture, 0, 0);

            // 一時RT解放
            cmd.ReleaseTemporaryRT(tempRTId);

            // 実行＆破棄
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }
    }

    public class Main : ResoniteMod
    {
        // --- Properties (Override) ----------------------------------------
        public override string Name => "Reso360Spout";
        public override string Author => "kka429";
        public override string Version => "0.0.6";
        public override string Link => "https://github.com/rassi0429/Reso360Spout";

        // --- Public Fields ------------------------------------------------
        public static ModConfiguration Config;
        public static Vector3 CameraOrigin;
        public static Quaternion CameraRotation;
        public static Vector3 CameraScale;

        // --- Config Keys --------------------------------------------------
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> SPOUT_ENABLE =
            new ModConfigurationKey<bool>("SPOUT_ENABLE", "spout enable", () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<ProjectionType> PROJECTION_TYPE =
            new ModConfigurationKey<ProjectionType>("PROJECTION_TYPE", "Projection Type",
                () => ProjectionType.Equirectangular_180);

        public enum CubeMapSize : int { Low = 512, Mid = 1024, High = 2048, Ultra = 3072 }

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<CubeMapSize> CUBEMAP_SIZE =
            new ModConfigurationKey<CubeMapSize>("CUBEMAP_SIZE",
                "cubemap size 512,1024,2048,3072",
                () => CubeMapSize.High);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<int2> OUTPUT_SIZE =
            new ModConfigurationKey<int2>("OUTPUT_SIZE", "output size",
                () => new int2(6144, 3072));

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> RENDER_IN_STEREO =
            new ModConfigurationKey<bool>("RENDER_IN_STEREO", "render in stereo",
                () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<float> NEAR_CLIP =
            new ModConfigurationKey<float>("NEAR_CLIP", "near clip",
                () => 0.01f);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<float> FAR_CLIP =
            new ModConfigurationKey<float>("FAR_CLIP", "far clip",
                () => 3000f);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> HIDE_LOCAL =
            new ModConfigurationKey<bool>("HIDE_LOCAL", "localを映さないようにする",
                () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> CAMERA_SLOT_NAME =
            new ModConfigurationKey<string>("CAMERA_SLOT_NAME", "Camera Slot Name", () => "#Camera");


        // --- Private Fields -----------------------------------------------
        private static GameObject _modEntry;
        private static UnityEntry _unityEntry;

        // --- Methods (ResoniteMod) ----------------------------------------
        public override void OnEngineInit()
        {
            // Harmony パッチ
            Harmony harmony = new Harmony("dev.kokoa.Reso360Spout");
            harmony.PatchAll();

            // Config
            Config = GetConfiguration();
            Config.OnThisConfigurationChanged += OnConfigChanged;
            Config.Save();

            // シーン情報デバッグ
            var scene = SceneManager.GetActiveScene();
            Main.Msg("[Reso360Spout] scene name: " + scene.name);

            // UnityEntry をホルダーになる GameObject に付与
            _modEntry = new GameObject("___MODEntry");
            _unityEntry = _modEntry.AddComponent<UnityEntry>();

            // カメラ座標等の初期値
            CameraOrigin = Vector3.zero;
            CameraRotation = Quaternion.identity;
            CameraScale = Vector3.one;
        }

        // --- Private Methods ----------------------------------------------
        private static void OnConfigChanged(ConfigurationChangedEvent configChangedEvent)
        {
            Main.Msg("[Reso360Spout] Config changed: " + configChangedEvent.Key.Name);

            try
            {
                // カメラの cullingMask 切替
                if (configChangedEvent.Key == HIDE_LOCAL)
                {
                    if (Config.GetValue(HIDE_LOCAL))
                {  
                        _unityEntry.CameraComponent.cullingMask &= ~((1 << 29) + (1 << 30) + (1 << 31)); // Turn bits 29-31 OFF
                        _unityEntry.CameraComponent.cullingMask &= ~(1 << 28); // Always turn bit 28 OFF
                    }
                    else
                    {
                        _unityEntry.CameraComponent.cullingMask |= ((1 << 29) + (1 << 30) + (1 << 31)); // Turn bits 29-31 ON
                        _unityEntry.CameraComponent.cullingMask &= ~(1 << 28); // Ensure bit 28 is always OFF
                    }
                }

                // クリップ平面
                if (configChangedEvent.Key == NEAR_CLIP)
                {
                    _unityEntry.CameraComponent.nearClipPlane = Config.GetValue(NEAR_CLIP);
                }
                if (configChangedEvent.Key == FAR_CLIP)
                {
                    _unityEntry.CameraComponent.farClipPlane = Config.GetValue(FAR_CLIP);
                }

                // Cubemap サイズ
                if (configChangedEvent.Key == CUBEMAP_SIZE)
                {
                    _unityEntry.cubeComponent.CubemapSize = (int)Config.GetValue(CUBEMAP_SIZE);
                }

                // 投影方式
                if (configChangedEvent.Key == PROJECTION_TYPE)
                {
                    _unityEntry.cubeComponent.ProjectionType = Config.GetValue(PROJECTION_TYPE);
                }

                // ステレオレンダリング
                if (configChangedEvent.Key == RENDER_IN_STEREO)
                {
                    _unityEntry.cubeComponent.RenderInStereo = Config.GetValue(RENDER_IN_STEREO);
                }

                // Spout の有効/無効切替
                if (configChangedEvent.Key == SPOUT_ENABLE)
                {
                    UpdateSpoutState();
                }
            }
            catch (Exception e)
            {
                Main.Msg("[Reso360Spout] Config Error occurred: " + e);
            }
        }

        /// <summary>
        /// Spout を有効/無効にする処理
        /// </summary>
        private static void UpdateSpoutState()
        {
            if (Config.GetValue(SPOUT_ENABLE))
            {
                var resolution = Config.GetValue(OUTPUT_SIZE);
                _unityEntry.Plugin = PluginEntry.CreateSender("VRCam", resolution.x, resolution.y);
                _unityEntry.SourceTexture = new UnityEngine.RenderTexture(resolution.x, resolution.y, 24);
                _unityEntry.cubeComponent.RenderTarget = _unityEntry.SourceTexture;
            }
            else
            {
                if (_unityEntry.Plugin != IntPtr.Zero)
                {
                    SpoutUtil.IssuePluginEvent(PluginEntry.Event.Dispose, _unityEntry.Plugin);
                }
                _unityEntry.Plugin = IntPtr.Zero;
                _unityEntry.SourceTexture = null;
                _unityEntry.cubeComponent.RenderTarget = null;
            }
        }

        // --- Harmony Patch ------------------------------------------------
        [HarmonyPatch(typeof(FrooxEngine.Engine), "RunUpdateLoop")]
        class Patch
        {
            static void Postfix(FrooxEngine.Engine __instance)
            {
                if (__instance.WorldManager.FocusedWorld == null) return;

                __instance.WorldManager.FocusedWorld.RunSynchronously(() =>
                {
                    // 重そうなのであとで最適化を検討
                    string cameraSlotName = Config.GetValue(CAMERA_SLOT_NAME);
                    var origin = __instance.WorldManager.FocusedWorld.RootSlot.FindChildInHierarchy(cameraSlotName);
                    if (origin != null)
                    {
                        CameraOrigin.Set(
                            origin.GlobalPosition.x,
                            origin.GlobalPosition.y,
                            origin.GlobalPosition.z
                        );
                        CameraRotation = new Quaternion(
                            origin.GlobalRotation.x,
                            origin.GlobalRotation.y,
                            origin.GlobalRotation.z,
                            origin.GlobalRotation.w
                        );
                        CameraScale.Set(
                            origin.GlobalScale.x,
                            origin.GlobalScale.y,
                            origin.GlobalScale.z
                        );
                    }
                });
            }
        }
    }
}
