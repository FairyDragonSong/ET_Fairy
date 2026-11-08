using System;
using ET.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ET
{
    /// <summary>Run the real Init -> ET Fiber -> YIUI panel path after every domain reload.</summary>
    [InitializeOnLoad]
    public static class PingDouEntrySmoke
    {
        private const string Active = "PingDou.EntrySmoke.Active";
        private const string Result = "PingDou.EntrySmoke.Result";
        private static int frames;
        private static bool tested;
        static PingDouEntrySmoke()
        {
            EditorApplication.update += Poll;
            Application.logMessageReceived += OnLog;
        }

        public static void Run()
        {
            SessionState.SetBool(Active, true);
            SessionState.SetString(Result, "");
            SessionState.SetFloat("PingDou.EntrySmoke.Start", (float)EditorApplication.timeSinceStartup);
            EditorSceneManager.OpenScene("Assets/Scenes/Init.unity");
            EditorApplication.isPlaying = true;
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (SessionState.GetBool(Active, false) && Application.isPlaying && (type == LogType.Exception || type == LogType.Error || type == LogType.Assert))
                SessionState.SetString(Result, message);
        }

        private static void Poll()
        {
            if (!SessionState.GetBool(Active, false))
                return;
            string error = SessionState.GetString(Result, "");
            if (error.Length > 0)
            {
                Finish(false, error);
                return;
            }

            if (EditorApplication.timeSinceStartup - SessionState.GetFloat("PingDou.EntrySmoke.Start", 0) > 150)
            {
                Finish(false, "Timed out waiting for the ET/YIUI PingDou view.");
                return;
            }

            if (!EditorApplication.isPlaying)
                return;
            var view = UnityEngine.Object.FindObjectOfType<PingDouView>();
            if (view == null || view.Session == null)
                return;
            try
            {
                if (!tested)
                {
                    if (view.GetComponent<Canvas>().rootCanvas.name == "PingDouGame(Clone)")
                        throw new Exception("ET UI mounted outside YIUI root.");
                    var scaler = view.GetComponent<Canvas>().rootCanvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                    if (scaler.referenceResolution != new Vector2(720, 1280))
                        throw new Exception("Portrait reference resolution was not applied.");
                    float zoom = view.Board.Zoom;
                    view.plus.onClick.Invoke();
                    if (view.Board.Zoom <= zoom)
                        throw new Exception("Zoom button did not invoke view command.");
                    view.resetView.onClick.Invoke();
                    view.moveMode.onClick.Invoke();
                    view.moveMode.onClick.Invoke();
                    view.album.onClick.Invoke();
                    if (!view.modal.activeSelf)
                        throw new Exception("Album did not open.");
                    view.modal.SetActive(false);
                    tested = true;
                }

                if (++frames < 60)
                    return;
                Finish(true, "ET Fiber, YIUI panel, portrait canvas, update loop and UI commands verified.");
            }
            catch (Exception e)
            {
                Finish(false, e.ToString());
            }
        }

        private static void Finish(bool success, string message)
        {
            SessionState.SetBool(Active, false);
            Debug.Log((success ? "PINGDOU_ET_ENTRY_PASS: " : "PINGDOU_ET_ENTRY_FAIL: ") + message);
            EditorApplication.isPlaying = false;
            EditorApplication.delayCall += () => EditorApplication.Exit(success ? 0 : 1);
        }
    }
}
