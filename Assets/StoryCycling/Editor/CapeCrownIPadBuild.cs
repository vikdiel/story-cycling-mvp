using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StoryCycling.Editor
{
    public static class CapeCrownIPadBuild
    {
        public const string ScenePath = "Assets/StoryCycling/Scenes/CampsBayTrainerRide.unity";
        public const string BundleId = "com.vikdiel.capecrown.unitybeta";

        [MenuItem("Story Cycling/iPad/Prepare Trainer Ride")]
        public static void Prepare()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before preparing iPad.");
            PlayerSettings.companyName = "Vikdiel";
            PlayerSettings.productName = "Cape Crown";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(NamedBuildTarget.iOS, 1); // ARM64
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Low);
            PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPadOnly;
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.iOS.requiresFullScreen = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS,new[] { GraphicsDeviceType.Metal });
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            // Preserve the developer's team ID. No credentials or signing keys belong here.
            PlayerSettings.bundleVersion = "0.3.0";
            PlayerSettings.iOS.buildNumber = "3";
            var mobile = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
            if (mobile == null) throw new InvalidOperationException("Mobile URP asset missing.");
            mobile.supportsHDR = false;
            mobile.msaaSampleCount = 2;
            mobile.renderScale = .85f;
            mobile.shadowDistance = 70;
            mobile.shadowCascadeCount = 1;
            var mobileData = new SerializedObject(mobile);
            mobileData.FindProperty("m_SoftShadowsSupported").boolValue = true;
            mobileData.ApplyModifiedPropertiesWithoutUndo();
            mobile.supportsCameraDepthTexture = false;
            mobile.supportsCameraOpaqueTexture = false;
            EditorUtility.SetDirty(mobile);
            if (!File.Exists(ScenePath)) CapeCrownSceneBuilder.BuildHillRide();
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath,true) };
            var plugin = AssetImporter.GetAtPath("Assets/StoryCycling/Plugins/iOS/CapeCrownBluetooth.mm") as PluginImporter;
            if(plugin == null) throw new InvalidOperationException("Bluetooth native plugin missing.");
            plugin.SetCompatibleWithAnyPlatform(false);
            plugin.SetCompatibleWithEditor(false);
            plugin.SetCompatibleWithPlatform(BuildTarget.iOS,true);
            plugin.SetPlatformData(BuildTarget.iOS,"CompileFlags","-fobjc-arc");
            plugin.SaveAndReimport();
            AssetDatabase.SaveAssets();
            Debug.Log("iPad visual beta configured: Landscape, ARM64/IL2CPP, Metal, Mobile URP. Native KICKR/HR bridge included; physical hardware validation pending.");
        }

        [MenuItem("Story Cycling/iPad/Export Xcode Project")]
        public static void Export()
        {
            Prepare();
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS,BuildTarget.iOS))
                throw new InvalidOperationException("Install iOS Build Support for Unity 6000.6.2f1 in Unity Hub, then restart the Editor.");
            // Versioned destination prevents accidental replacement of a hand-signed Xcode export.
            string path = "Builds/iPad/CapeCrown-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { ScenePath }, target = BuildTarget.iOS,
                locationPathName = path, options = BuildOptions.Development
            });
            string projectFile = Path.Combine(path,"Unity-iPhone.xcodeproj/project.pbxproj");
            bool complete = report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0 &&
                report.summary.platform == BuildTarget.iOS && File.Exists(projectFile) && File.Exists(Path.Combine(path,"Info.plist"));
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/ipad-export.txt",$"Gate: {(complete ? "PASS" : "FAIL")}\nUnity result: {report.summary.result}\nPlatform: {report.summary.platform}\nPath: {path}\nSize: {report.summary.totalSize}\nErrors: {report.summary.totalErrors}\nXcode project exists: {File.Exists(projectFile)}\nUnity export only; Xcode compile/signing and iPad performance not tested.\n");
            if (!complete)
                throw new BuildFailedException("iPad export incomplete. See Logs/ipad-export.txt and Unity Console. If iOS support was just installed, restart Unity before retrying.");
            Debug.Log("iPad Xcode export PASS: " + Path.GetFullPath(path) + "/Unity-iPhone.xcodeproj");
        }
    }
}
