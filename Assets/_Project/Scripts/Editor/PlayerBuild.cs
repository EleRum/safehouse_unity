using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Safehouse.EditorTools
{
    /// <summary>
    /// One-click Windows build that also ships the things the exe cannot make for itself.
    ///
    /// Two facts drive this file:
    ///  - the scene list comes from <c>EditorBuildSettings</c>, so a build can never silently contain zero scenes
    ///    (which produces an exe that starts, shows black, and looks like a game that "did not compile");
    ///  - <c>icon_cache/</c> is read from beside the executable at run time (<see cref="Safehouse.Core.IconLibrary"/>),
    ///    it is git-ignored and it is not in StreamingAssets, so a build has to copy it next to the .exe or every
    ///    item, portrait and trader comes out blank.
    /// </summary>
    public static class PlayerBuild
    {
        private const string OutputFolder = @"D:\file\build\safehouse";
        private const string Executable = "Safehouse.exe";

        [MenuItem("Safehouse/Build Windows x64")]
        public static void BuildWindowsFromMenu()
        {
            var report = BuildWindows();
            EditorUtility.DisplayDialog("Safehouse build",
                report == null ? "The build failed. See the console." :
                $"Built {report.summary.totalSize / (1024 * 1024)} MB in {report.summary.totalTime.TotalSeconds:F0}s\n" +
                OutputFolder,
                "OK");
        }

        [MenuItem("Safehouse/Open build folder")]
        public static void OpenBuildFolder()
        {
            Directory.CreateDirectory(OutputFolder);
            EditorUtility.RevealInFinder(OutputFolder);
        }

        /// <summary>The batch entry point: <c>-executeMethod Safehouse.EditorTools.PlayerBuild.BuildWindows</c>.</summary>
        public static BuildReport BuildWindows()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new BuildFailedException(
                    "No scenes are enabled in Build Profiles; the build would start into an empty screen.");
            }

            Directory.CreateDirectory(OutputFolder);
            // NamedBuildTarget is the Unity 6 spelling: the BuildTargetGroup overloads are obsolete and print a
            // warning on every compile.
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(OutputFolder, Executable),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"Build {report.summary.result}: " +
                               $"{report.summary.totalErrors} errors, {report.summary.totalWarnings} warnings.");
                throw new BuildFailedException($"The build did not succeed: {report.summary.result}.");
            }

            CopyIcons();
            CopyReadme();
            Debug.Log($"Build ok: {report.summary.totalSize / (1024 * 1024)} MB in " +
                      $"{report.summary.totalTime.TotalSeconds:F0}s -> {options.locationPathName}");
            return report;
        }

        /// <summary>
        /// icon_cache sits beside the exe because that is where IconLibrary looks in a build. Missing icons are not
        /// a build error - the game runs - but a release with none of them is a release nobody wants, so it is a
        /// loud warning rather than silence.
        /// </summary>
        private static void CopyIcons()
        {
            var source = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "icon_cache"));
            if (!Directory.Exists(source))
            {
                Debug.LogWarning($"icon_cache not found at {source}: the build will show no icons, portraits or " +
                                 "trader faces. Run the Python import tools first, then build again.");
                return;
            }

            var target = Path.Combine(OutputFolder, "icon_cache");
            CopyTree(source, target);
            Debug.Log($"icon_cache copied to {target}");
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var folder in Directory.GetDirectories(source))
            {
                CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
            }
        }

        /// <summary>A note next to the exe: what to keep together, and where this build writes its saves.</summary>
        private static void CopyReadme()
        {
            var lines = new List<string>
            {
                "SAFEHOUSE - Windows x64 build",
                "",
                "Keep this folder together: Safehouse.exe needs Safehouse_Data and UnityPlayer.dll,",
                "and icon_cache supplies every item, portrait and trader picture.",
                "",
                "Saves, GM scenes and GM quests are written to:",
                "  " + Path.Combine(Application.persistentDataPath, "Safehouse"),
                "",
                "To keep saves inside this folder instead, set the environment variable",
                "SAFEHOUSE_UNITY_DATA_DIR to a path before starting the game.",
                "",
                $"Built {DateTime.Now:yyyy-MM-dd HH:mm} from {Application.unityVersion}.",
            };

            File.WriteAllLines(Path.Combine(OutputFolder, "READ-ME.txt"), lines);
        }
    }
}
