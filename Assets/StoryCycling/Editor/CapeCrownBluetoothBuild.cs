#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace StoryCycling.Editor
{
    public static class CapeCrownBluetoothBuild
    {
        [PostProcessBuild(200)]
        public static void Configure(BuildTarget target,string path)
        {
            if(target!=BuildTarget.iOS)return;
            string projectPath=PBXProject.GetPBXProjectPath(path);
            var project=new PBXProject();project.ReadFromFile(projectPath);
            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(),"CoreBluetooth.framework",false);
            project.WriteToFile(projectPath);
            string plistPath=Path.Combine(path,"Info.plist");var plist=new PlistDocument();plist.ReadFromFile(plistPath);
            plist.root.SetString("NSBluetoothAlwaysUsageDescription","Verbinde deinen Wahoo KICKR Core und Brustgurt, um deine Fahrt und deinen Puls anzuzeigen.");
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
