using System;
using System.IO;
using ET.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YooAsset;

namespace ET
{
    public static class PingDouEditor
    {
        private const string Root = "Assets/GameRes/PingDou";
        private const string ResourcesRoot = "Assets/Resources/PingDou";
        [MenuItem("ET/PingDou/Rebuild assets")]
        public static void BuildAssets()
        {
            Directory.CreateDirectory(Root + "/Meshes");
            Directory.CreateDirectory(Root + "/Materials");
            Directory.CreateDirectory(Root + "/UI");
            AssetDatabase.Refresh();
            var config = AssetDatabase.LoadAssetAtPath<PingDouAssets>(ResourcesRoot + "/Assets.asset");
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<PingDouAssets>();
                AssetDatabase.CreateAsset(config, ResourcesRoot + "/Assets.asset");
            }

            config.catalog = AssetDatabase.LoadAssetAtPath<TextAsset>(ResourcesRoot + "/Catalog.json");
            config.font = AssetDatabase.LoadAssetAtPath<Font>(Root + "/UI/PingDouSans.otf");
            config.plastic = Save(new Material(Shader.Find("PingDou/Plastic")) { name = "Plastic", enableInstancing = true }, Root + "/Materials/Plastic.mat");
            config.flat = Save(new Material(Shader.Find("Unlit/Color")) { name = "Flat" }, Root + "/Materials/Flat.mat");
            config.translucent = Save(new Material(Shader.Find("PingDou/Translucent")) { name = "Translucent" }, Root + "/Materials/Translucent.mat");
            config.beadHeatStages = new Mesh[17];
            for (int i = 0; i < 17; ++i)
                config.beadHeatStages[i] = Save(PingDouMesh.Bead(i / 16f), Root + "/Meshes/Bead-" + i.ToString("00") + ".asset");
            config.peg = Save(PingDouMesh.Peg(), Root + "/Meshes/Peg.asset");
            config.pen = Save(PingDouMesh.Pen(), Root + "/Meshes/Pen.asset");
            config.iron = Save(PingDouMesh.Iron(), Root + "/Meshes/Iron.asset");
            config.ring = Save(PingDouMesh.Ring(.43f, .40f), Root + "/Meshes/TargetRing.asset");
            config.targetFill = Save(PingDouMesh.Ring(.39f, 0), Root + "/Meshes/TargetFill.asset");
            config.roundedPanel = MakeSprite("RoundedPanel", false);
            config.softCircle = MakeSprite("Circle", true);
            config.tap = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/Audio/bead-tap.wav");
            config.complete = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/Audio/complete.wav");
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            var view = PingDouUI.Build(config);
            PrefabUtility.SaveAsPrefabAsset(view.gameObject, ResourcesRoot + "/PingDouGame.prefab");
            UnityEngine.Object.DestroyImmediate(view.gameObject);
            BuildPreviewScene();
            var global = AssetDatabase.LoadAssetAtPath<GlobalConfig>("Assets/Resources/GlobalConfig.asset");
            global.EnableDll = false;
            global.EPlayMode = EPlayMode.EditorSimulateMode;
            EditorUtility.SetDirty(global);
            AssetDatabase.SaveAssets();
            Debug.Log("PINGDOU_ASSETS_READY");
        }

        private static T Save<T>(T obj, string path)
            where T : UnityEngine.Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(obj, existing);
                UnityEngine.Object.DestroyImmediate(obj);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(obj, path);
            return obj;
        }

        private static Sprite MakeSprite(string name, bool circle)
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; ++y)
                for (int x = 0; x < size; ++x)
                {
                    Vector2 p = new Vector2(x + .5f - size * .5f, y + .5f - size * .5f);
                    float distance = circle ? p.magnitude - 30 : new Vector2(Mathf.Max(Mathf.Abs(p.x) - 16, 0), Mathf.Max(Mathf.Abs(p.y) - 16, 0)).magnitude - 15;
                    texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(.5f - distance)));
                }

            texture.Apply();
            string path = Root + "/UI/" + name + ".png";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteBorder = circle ? Vector4.zero : new Vector4(24, 24, 24, 24);
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static void BuildPreviewScene()
        {
            var previous = SceneManager.GetActiveScene();
            bool additive = !string.IsNullOrEmpty(previous.path);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, additive ? NewSceneMode.Additive : NewSceneMode.Single);
            SceneManager.SetActiveScene(scene);
            var bootstrap = new GameObject("PingDou art preview").AddComponent<PingDouPreview>();
            bootstrap.view = AssetDatabase.LoadAssetAtPath<GameObject>(ResourcesRoot + "/PingDouGame.prefab").GetComponent<PingDouView>();
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            new GameObject("AudioListener", typeof(AudioListener));
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/PingDouPreview.unity");
            if (additive)
            {
                EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid())
                    SceneManager.SetActiveScene(previous);
            }
        }

        [MenuItem("ET/PingDou/Open gameplay (ET + YIUI)")]
        public static void OpenGame()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Init.unity");
            EditorApplication.isPlaying = true;
        }

        [MenuItem("ET/PingDou/Open art preview")]
        public static void OpenPreview()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/PingDouPreview.unity");
            EditorApplication.isPlaying = true;
        }

        // Automated visual QA uses the same prefab, meshes, round and presenter as the ET entry.
        public static void BuildAndVerify()
        {
            try
            {
                BuildAssets();
                VerifyAndCapture();
                Debug.Log("PINGDOU_VERIFICATION_PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        private static void VerifyAndCapture()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ResourcesRoot + "/PingDouGame.prefab");
            var view = UnityEngine.Object.Instantiate(prefab).GetComponent<PingDouView>();
            var patterns = PingDouCatalog.Load(view.assets.catalog);
            var session = new PingDouSession(patterns, new PingDouSaveStore("PingDou.QA."));
            session.Open(0, true);
            view.Initialize(session);
            Canvas.ForceUpdateCanvases();
            view.Tick(.016f);
            int done = 0;
            for (int i = 0; i < patterns[0].size * patterns[0].size && done < 20; ++i)
                if (session.Round.Key(i) != '.')
                {
                    view.Board.Queue(i, session.Round.Key(i));
                    ++done;
                }

            view.Tick(.35f);
            Canvas.ForceUpdateCanvases();
            view.Tick(.016f);
            Capture(view, "01-combo20");
            if (session.Round.Placed != 20 || session.Round.BaseScore + session.Round.ComboScore != 270)
                throw new Exception("Presenter landing/score mismatch");
            // Render a drop above a visible center cell, then verify that scoring waits for landing.
            int dropIndex = -1;
            for (int i = 0; i < patterns[0].size * patterns[0].size; ++i)
                if (session.Round.CanPlace(i, session.Round.Key(i)) && Mathf.Abs(view.Board.Cell(i).x) < 4 && Mathf.Abs(view.Board.Cell(i).y) < 4)
                {
                    dropIndex = i;
                    break;
                }

            view.Board.Queue(dropIndex, session.Round.Key(dropIndex));
            view.Tick(.10f);
            Capture(view, "00-placement");
            if (session.Round.Placed != 20)
                throw new Exception("Bead scored before landing.");
            view.Tick(.3f);
            // Both screen mapping and zoom anchoring must refer to the same board plane.
            var uiCamera = view.GetComponent<Canvas>().rootCanvas.worldCamera;
            Vector2 screenCenter = RectTransformUtility.WorldToScreenPoint(uiCamera, view.detailImage.rectTransform.position);
            Vector2 beforeZoom = view.Board.WorldAt(screenCenter, uiCamera);
            view.Board.ZoomAt(1.2f, screenCenter, uiCamera);
            if (Vector2.Distance(beforeZoom, view.Board.WorldAt(screenCenter, uiCamera)) > .02f)
                throw new Exception("Zoom center drift.");
            view.Board.ResetView();
            for (int i = 0; i < patterns[0].size * patterns[0].size; ++i)
                session.Commit(i);
            session.BeginIroning();
            for (int i = 0; i < patterns[0].size * patterns[0].size; ++i)
                if (i % patterns[0].size < 12)
                    session.Heat(i, 1);
            view.Tick(.016f);
            Capture(view, "02-ironing");
            for (int i = 0; i < patterns[0].size * patterns[0].size; ++i)
                session.Heat(i, 1);
            if (session.Round.HeatPercent != 100)
                throw new Exception("Heating never completes");
            session.Reveal();
            view.Tick(1.1f);
            Canvas.ForceUpdateCanvases();
            view.Tick(.016f);
            Capture(view, "03-result");
            session.Next();
            view.Tick(.016f);
            if (session.Round.Placed != 0 || session.Round.Pattern.id == patterns[0].id)
                throw new Exception("Next pattern did not reset round");
            view.OpenAlbum();
            Canvas.ForceUpdateCanvases();
            view.Tick(.016f);
            Capture(view, "04-album");
            view.Shutdown();
            UnityEngine.Object.DestroyImmediate(view.gameObject);
        }

        private static void Capture(PingDouView view, string name)
        {
            Directory.CreateDirectory("../Book/PingDou/QA");
            var canvas = view.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            var camera = new GameObject("QA UI camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -100);
            camera.orthographic = true;
            camera.orthographicSize = 640;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = PingDouUI.Cream;
            camera.cullingMask = 1 << 5;
            canvas.worldCamera = camera;
            canvas.planeDistance = 20;
            var rt = new RenderTexture(720, 1280, 24);
            camera.targetTexture = rt;
            rt.Create();
            Canvas.ForceUpdateCanvases();
            view.Tick(.016f);
            camera.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var image = new Texture2D(720, 1280, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 720, 1280), 0, 0);
            image.Apply();
            File.WriteAllBytes("../Book/PingDou/QA/" + name + ".png", image.EncodeToPNG());
            RenderTexture.active = previous;
            camera.targetTexture = null;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(camera.gameObject);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        }
    }
}
