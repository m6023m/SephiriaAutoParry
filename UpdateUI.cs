using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;
using SephiriaAutoParry.Updates;

namespace SephiriaAutoParry
{
    internal sealed class UpdateUI : MonoBehaviour
    {
        internal static UpdateUI Instance;
        internal static ConfigEntry<bool> CheckOnStartup;
        private Task<Manifest> checking;
        private Task downloading;
        private Manifest offered;
        private UI_MessageBox workingBox;
        private string message;
        private bool offerReady, manualCheck;
        private string Cache { get { return Path.Combine(Paths.CachePath, "SephiriaAutoParry"); } }

        private void Start()
        {
            Instance = this;
            string failed = Path.Combine(Cache, "failed.txt");
            if (File.Exists(failed))
            {
                message = "업데이트를 적용하지 못했습니다. 기존 버전을 유지합니다.\n자세한 내용은 BepInEx 로그에서 확인할 수 있습니다.";
                try { File.Delete(failed); } catch (IOException) { }
            }
            if (UpdateCore.HasPending(Cache))
                message = "다운로드된 업데이트가 있습니다. 게임을 다시 시작하면 적용됩니다.";
            else if (CheckOnStartup.Value) Check(false);
        }

        internal void Check(bool manual)
        {
            if (checking != null || downloading != null) return;
            manualCheck = manual;
            if (UpdateCore.HasPending(Cache))
            {
                message = "업데이트가 준비되었습니다. 게임을 다시 시작하면 적용됩니다.";
                return;
            }
            checking = Task.Factory.StartNew(() => UpdateCore.Check(Plugin.Version));
        }

        private void Update()
        {
            if (checking != null && checking.IsCompleted)
            {
                if (checking.IsFaulted)
                {
                    Plugin.Log("Update check failed; current version preserved: " + checking.Exception.GetBaseException().Message);
                    if (manualCheck) message = "업데이트 확인에 실패했습니다. 현재 버전은 그대로 사용할 수 있습니다.\n인터넷 연결을 확인하고 나중에 다시 시도해 주세요.";
                }
                else
                {
                    offered = checking.Result;
                    offerReady = offered != null;
                    Plugin.Log(offered == null ? "Updater: latest stable version installed (" + Plugin.Version + ")" : "Updater: available " + offered.version);
                    if (!offerReady && manualCheck) message = "최신 버전입니다.\n현재 버전: " + Plugin.Version;
                }
                checking = null;
            }
            if (downloading != null && downloading.IsCompleted)
            {
                if (workingBox) { workingBox.ForceClose(); workingBox = null; }
                if (downloading.IsFaulted)
                {
                    Plugin.Log("Update download failed; current version preserved: " + downloading.Exception.GetBaseException());
                    message = "다운로드 또는 파일 검증에 실패했습니다. 기존 버전을 유지합니다.\n나중에 다시 시도하거나 GitHub Releases에서 수동 설치해 주세요.";
                }
                else message = "업데이트 다운로드와 검증이 완료되었습니다.\n게임을 다시 시작하면 " + offered.version + " 버전이 적용됩니다.\n지금은 현재 버전으로 계속 플레이할 수 있습니다.";
                downloading = null;
            }
            if (!UIManager.Instance) return;
            var holder = UIManager.Instance.GetElement<UI_MessageBoxHolder>();
            if (!holder || holder.HasOpenedBox) return;
            if (message != null)
            {
                string body = message;
                message = null;
                holder.OpenYes("자동 패링 업데이트\n\n" + body, () => { });
            }
            else if (offerReady)
            {
                offerReady = false;
                var box = holder.OpenYesNo("자동 패링 업데이트\n\n새 버전이 있습니다.\n현재 " + Plugin.Version + " → 새 버전 " + offered.version + "\n업데이트를 다운로드할까요? 적용은 다음 실행 때 이루어집니다.", BeginDownload, () => { }) as UI_MessageBox_YesNo;
                if (box) { Label(box.yesButton, "업데이트"); Label(box.noButton, "나중에"); }
            }
            else if (downloading != null && !workingBox)
            {
                workingBox = holder.OpenYes("자동 패링 업데이트\n\n받는 중…\n이 창을 닫고 게임을 계속할 수 있습니다. 완료되면 다시 알려드립니다.", () => { });
            }
        }

        private void BeginDownload()
        {
            if (!File.Exists(Path.Combine(Paths.PatcherPluginPath, "SephiriaAutoParry.Updater.dll")))
            {
                message = "업데이트 적용기가 없습니다. GitHub Releases의 설치 ZIP을 다시 설치해 주세요.";
                return;
            }
            string cache = Cache;
            var manifest = offered;
            downloading = Task.Factory.StartNew(() => UpdateCore.Stage(cache, manifest));
        }

        private static void Label(Button button, string text)
        {
            foreach (var localization in button.GetComponentsInChildren<UI_LocalizationStringText>(true)) localization.enabled = false;
            foreach (var label in button.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true)) label.text = text;
        }
    }
}
