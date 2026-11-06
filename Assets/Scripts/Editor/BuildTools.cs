using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pitzutz.EditorTools
{
    public class ArtImport : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/Resources/Art/")) return;
            var t = (TextureImporter)assetImporter;
            t.textureType = TextureImporterType.Default;
            t.filterMode = FilterMode.Bilinear; t.mipmapEnabled = false; t.alphaIsTransparency = true;
            // Backdrops are the only big pictures, and soft enough to compress; small sprites keep their clean edges
            t.textureCompression = Path.GetFileName(assetPath).StartsWith("bg_") ? TextureImporterCompression.Compressed : TextureImporterCompression.Uncompressed; t.npotScale = TextureImporterNPOTScale.None;
            t.wrapMode = TextureWrapMode.Clamp;
        }
    }

    public class AudioImport : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith("Assets/Resources/Audio/")) return;
            var a = (AudioImporter)assetImporter;
            bool music = Path.GetFileName(assetPath).StartsWith("m_");
            var s = a.defaultSampleSettings;
            s.compressionFormat = AudioCompressionFormat.Vorbis; s.quality = .6f;
            s.loadType = music ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            a.defaultSampleSettings = s;
        }
    }

    public static class BuildTools
    {
        public const string Version = "1.0.0";
        const string Scene = "Assets/Scenes/Main.unity", Report = "verification/release";

        static void Common()
        {
            PlayerSettings.productName = "פיצוץ גדול";
            PlayerSettings.companyName = "Pitzutz";
            PlayerSettings.bundleVersion = Version;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.defaultScreenWidth = 1920; PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.runInBackground = true;
            PlayerSettings.resizableWindow = true;
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Icons/icon_full.png");
            if (icon != null) PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Application);
        }

        public static void BuildLinux() => Build("Builds/linux/PitzutzGadol.x86_64", BuildTarget.StandaloneLinux64);
        public static void BuildWindows() => Build("Builds/windows/PitzutzGadol.exe", BuildTarget.StandaloneWindows64);
        /// For trying things out only; never published, so it skips the gates.
        public static void BuildLinuxTest() { skipCheck = true; Build("Builds/test-linux/PitzutzGadol.x86_64", BuildTarget.StandaloneLinux64); }
        static bool skipCheck;

#if UNITY_ANDROID
        /// Unity writes these into the manifest with no setting to change them.
        class AndroidManifestFix : UnityEditor.Android.IPostGenerateGradleAndroidProject
        {
            public int callbackOrder => 0;

            public void OnPostGenerateGradleAndroidProject(string path)
            {
                string file = Path.Combine(path, "src/main/AndroidManifest.xml");
                string xml = File.ReadAllText(file);
                string Need(string text, string from, string to)
                {
                    if (text.Contains(to)) return text;   // an incremental build keeps the patched file
                    if (!text.Contains(from)) throw new BuildFailedException("Manifest has no " + from);
                    return text.Replace(from, to);
                }
                // Predictive back would close the app without ever telling it
                xml = Need(xml, "android:enableOnBackInvokedCallback=\"true\"", "android:enableOnBackInvokedCallback=\"false\"");
                xml = Need(xml, "android:name=\"com.unity3d.player.UnityPlayerGameActivity\"", "android:name=\"com.ibfix.pitzutzgadol.GameMain\"");
                xml = Need(xml, "android:name=\"unity.run-without-focus\" android:value=\"true\"", "android:name=\"unity.run-without-focus\" android:value=\"false\"");
                if (!xml.Contains("android.permission.VIBRATE")) xml = xml.Replace("<application", "<uses-permission android:name=\"android.permission.VIBRATE\" />\n  <application");
                File.WriteAllText(file, xml);
            }
        }

        /// Run with -buildTarget Android. The keystore password comes only from the environment, see tools/build_android.sh.
        public static void BuildAndroid()
        {
            var id = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(id, "com.ibfix.pitzutzgadol");
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.SetScriptingBackend(id, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(id, ManagedStrippingLevel.Minimal);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            // 34 keeps the Back key reaching the game: from 36 the system takes it over for predictive back
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.Android.startInFullscreen = true;
            PlayerSettings.Android.renderOutsideSafeArea = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            EditorUserBuildSettings.buildAppBundle = false;
            SetIcons(id);

            string pass = Environment.GetEnvironmentVariable("PITZUTZ_KEYSTORE_PASS");
            if (string.IsNullOrEmpty(pass)) { Debug.LogError("PITZUTZ_KEYSTORE_PASS is not set"); EditorApplication.Exit(1); return; }
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = "Keystore/pitzutz-gadol.keystore";
            PlayerSettings.Android.keyaliasName = "pitzutzgadol";
            PlayerSettings.Android.keystorePass = pass;
            PlayerSettings.Android.keyaliasPass = pass;
            Directory.CreateDirectory("Builds/android");
            AssetDatabase.SaveAssets();
            Build("Builds/android/PitzutzGadol-android.apk", BuildTarget.Android);
            PlayerSettings.Android.keystorePass = ""; PlayerSettings.Android.keyaliasPass = "";
            AssetDatabase.SaveAssets();
        }

        /// Trying the Android pipeline out while stages are still being tuned; never published.
        public static void BuildAndroidTest() { skipCheck = true; BuildAndroid(); }

        static void SetIcons(NamedBuildTarget id)
        {
            Texture2D Load(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Icons/" + name + ".png");
            var adaptive = PlayerSettings.GetPlatformIcons(id, UnityEditor.Android.AndroidPlatformIconKind.Adaptive);
            foreach (var i in adaptive) i.SetTextures(Load("icon_bg"), Load("icon_fg"));
            PlayerSettings.SetPlatformIcons(id, UnityEditor.Android.AndroidPlatformIconKind.Adaptive, adaptive);
        }
#endif

        /// Plays every stage with the real rules. A stage the bot can not win stops the build.
        public static void Validate()
        {
            string stamp = "Library/stages_passed.txt", now = Fingerprint("Assets/Scripts/Sim");
            if (File.Exists(stamp) && File.ReadAllText(stamp) == now) { Debug.Log("VALIDATE OK (nothing changed since it last passed)"); return; }
            Directory.CreateDirectory(Report);
            bool ok = Validator.Run(Report + "/stages_report.txt", s => Debug.Log("VALIDATE " + s));
            if (!ok) throw new BuildFailedException("A stage failed, see " + Report + "/stages_report.txt");
            File.WriteAllText(stamp, now);
        }

        static string Fingerprint(params string[] folders)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var all = new System.Text.StringBuilder();
            foreach (string dir in folders)
                foreach (string f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(n => n, StringComparer.Ordinal))
                    all.Append(f).Append(BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(f))));
            return all.ToString();
        }

        static void Build(string path, BuildTarget target)
        {
            Common();
            try { if (!skipCheck) { Validate(); AudioBefore(); } }
            catch (BuildFailedException e) { Debug.LogError(e.Message); EditorApplication.Exit(1); return; }
            var r = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { Scene }, locationPathName = path, target = target, options = BuildOptions.None });
            Debug.Log("BUILD " + r.summary.result + " size=" + r.summary.totalSize);
            if (r.summary.result != BuildResult.Succeeded) { EditorApplication.Exit(1); return; }
            try { if (!skipCheck) After(path, target); }
            catch (BuildFailedException e) { Debug.LogError("GATE FAILED: " + e.Message); EditorApplication.Exit(1); }
        }

        const string AudioDir = "Assets/Resources/Audio", Stamp = "Library/player_passed.txt";

        static string[] AudioFiles() => Directory.GetFiles(AudioDir).Where(f => !f.EndsWith(".meta")).Select(f => f.Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        /// Checks that need no player: the sound list, the files, the project settings and the default volumes.
        static void AudioBefore()
        {
            var listed = new HashSet<string>(Sound.Effects.Concat(Sound.Tracks));
            var files = AudioFiles();
            var names = new HashSet<string>(files.Select(Path.GetFileNameWithoutExtension));
            foreach (string name in listed.Except(names)) throw new BuildFailedException("Sound listed but no file: " + name);
            foreach (string name in names.Except(listed)) throw new BuildFailedException("Sound file not in Sound.cs: " + name);

            foreach (string f in files)
            {
                using (var read = File.OpenRead(f))
                {
                    var head = new byte[24];
                    read.Read(head, 0, head.Length);
                    if (System.Text.Encoding.ASCII.GetString(head).StartsWith("version https://git-lfs")) throw new BuildFailedException("Sound is only a Git LFS pointer: " + f);
                }
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(f);
                if (clip == null || clip.length <= 0) throw new BuildFailedException("Sound does not import: " + f);
            }

            var asked = new HashSet<string>(Stages.All.Select(l => l.music));
            foreach (string code in Directory.GetFiles("Assets/Scripts", "*.cs"))
                foreach (Match m in Regex.Matches(File.ReadAllText(code), "Sound\\.(?:Play|Music)\\(\"([^\"]+)\""))
                    asked.Add(m.Groups[1].Value);
            foreach (string name in asked.Except(listed)) throw new BuildFailedException("Code plays a sound that does not exist: " + name);

            var fresh = new Save();
            if (fresh.music <= 0 || fresh.sfx <= 0) throw new BuildFailedException("Default volume is 0");

            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);
            if (settings.FindProperty("m_DisableAudio").boolValue) throw new BuildFailedException("Audio is disabled in the project settings");
            if (settings.FindProperty("m_Volume").floatValue <= 0) throw new BuildFailedException("Project audio volume is 0");
            Debug.Log("AUDIO sound list, files, settings and default volumes OK");
        }

        /// Gates on the built player. Linux runs here; Windows and Android need a pass of the same code on Linux.
        static void After(string path, BuildTarget target)
        {
            string now = Fingerprint("Assets/Scripts") + string.Join(",", AudioFiles());
            if (target != BuildTarget.StandaloneLinux64)
            {
                if (!File.Exists(Stamp) || File.ReadAllText(Stamp) != now) throw new BuildFailedException("The player checks have not passed on this code: build Linux first");
                Debug.Log("GATE passed on the Linux build of the same code");
                return;
            }
            Directory.CreateDirectory(Report);
            PlayerTest(path, "-uicheck", "ui_report.txt", "");
            PlayerTest(path, "-uicheck", "ui_report_touch.txt", "-touchui", "ui_report.txt");
            PlayerTest(path, "-soaktest", "soak_report.txt", "");
            PlayerTest(path, "-audiotest", "audio_report.txt", "");
            PlayerTest(path, "-touchtest", "touch_report.txt", "");
            PlayerTest(path, "-inputtest", "input_report.txt", "");
            PlayerTest(path, "-flowtest", "flow_report.txt", "");
            File.WriteAllText(Stamp, now);
            Debug.Log("GATE all player checks passed");
        }

        static void PlayerTest(string path, string flag, string reportName, string more, string writes = null)
        {
            string dir = Path.GetFullPath(Report), written = Path.Combine(dir, writes ?? reportName), report = Path.Combine(dir, reportName);
            File.Delete(written);
            var run = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(path), $"-batchmode -nographics {flag} {more} -captureDir \"{dir}\" -logFile \"{dir}/{flag.Substring(1)}_player.log\"") { UseShellExecute = false };
            run.Environment["PULSE_SERVER"] = "/nonexistent";   // the measurement is inside the player, so the build need not play through the speakers
            using var player = System.Diagnostics.Process.Start(run);
            if (!player.WaitForExit(240000)) { player.Kill(); throw new BuildFailedException(flag + " did not finish"); }
            string text = File.Exists(written) ? File.ReadAllText(written) : "";
            if (writes != null && File.Exists(written)) File.Copy(written, report, true);
            if (!text.Contains("ALL PASS") || player.ExitCode != 0) throw new BuildFailedException(flag + " failed, see " + report);
            Debug.Log("GATE " + flag + " " + more + " ALL PASS");
        }
    }
}
