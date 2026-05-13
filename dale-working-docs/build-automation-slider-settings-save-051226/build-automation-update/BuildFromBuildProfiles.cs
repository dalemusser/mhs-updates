using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Profile;
using System.Collections.Generic;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using System.IO;
using System;
using Unity.BuildReportInspector;

namespace MHS
{
    public class BuildFromBuildProfiles : IPostprocessBuildWithReport
    {

        #region Build Profile References
        private static string Unit1 = "Unit 1 Web";
        private static string Unit2 = "Unit 2 Web";
        private static string Unit3 = "Unit 3 Web";
        private static string Unit4 = "Unit 4 Web";
        private static string Unit5 = "Unit 5 Web";

        private static string Unit1SeparateAudioResources = "Unit 1 Web Resize Test";
        private static string Unit2SeparateAudioResources = "Unit 2 Web Resize Test";
        private static string Unit3SeparateAudioResources = "Unit 3 Web Resize Test";
        private static string Unit4SeparateAudioResources = "Unit 4 Web Resize Test";
        private static string Unit5SeparateAudioResources = "Unit 5 Web Resize Test";
        private static string MHSUnitLoader = "MHS Unit Loader";


        #endregion

        public static string BuildPathBaseDirectory = "Builds/WebGLBuild";


        #region Build Reporting
        /// <summary>
        /// These fields help us find the last created build report file in the local library folder
        /// and place it in the project.
        /// Inspired by the Unity Build Report Inspector BuildResportInsector.cs
        /// </summary>
        static readonly string k_BuildReportDirectory = "Assets/BuildReports";
        static readonly string k_LastBuildReportFileName = "Library/LastBuild.buildreport";
        #endregion

        #region  Addressable Build Settings
        public static string build_script
             = "Assets/Data/AddressableAssetsData/DataBuilders/BuildScriptPackedMode.asset";

        public static string settings_asset
            = "Assets/Data/AddressableAssetsData/AddressableAssetSettings.asset";

        /// <summary>
        /// If you need to change the current Addressables Content profile do so here. 
        /// </summary>
        public static string profile_name = "CloudFront";
        private static AddressableAssetSettings settings;

        #endregion

        private static string _version;
        private static string _buildPath;

        public int callbackOrder => 1;

        [MenuItem("Build/All Units In Single Build")]
        public static void BuildAllUnitsInSingleBuild()
        {
            BuildFromProfile("All Units Web Build");
        }

        [MenuItem("Build/Build Unit 1 from Profile")]
        public static void BuildUnit1()
        {
            BuildFromProfile(Unit1);
        }

        [MenuItem("Build/Build Unit 2 from Profile")]
        public static void BuildUnit2()
        {
            BuildFromProfile(Unit2);
        }

        [MenuItem("Build/Build Unit 3 from Profile")]
        public static void BuildUnit3()
        {
            BuildFromProfile(Unit3);
        }
        [MenuItem("Build/Build Unit 4 from Profile")]
        public static void BuildUnit4()
        {
            BuildFromProfile(Unit4);
        }
        [MenuItem("Build/Build Unit 5 from Profile")]
        public static void BuildUnit5()
        {
            BuildFromProfile(Unit5);
        }

        [MenuItem("Build/Build Units 1 and 2 from Profiles")]
        public static void BuildUnits1And2()
        {
            BuildUnit1();
            BuildUnit2();
        }

        [MenuItem("Build/Build Units From Profiles")]
        public static void BuildUnits()
        {
            BuildUnit1();
            BuildUnit2();
            BuildUnit3();
            BuildUnit4();
            BuildUnit5();
        }

        [MenuItem("Build/Build All Units Web Build")]
        public static void BuildSingleBuildWithAllUnits()
        {
            BuildFromProfile("All Units Web Build");
        }

        [MenuItem("Build/Build All Units Web Build Debug")]
        public static void DebugBuildSingleBuildWithAllUnits()
        {
            BuildFromProfile("All Units Web Build Debug");
        }

        [MenuItem("Build/Build All Units and Player Sandbox")]
        public static void BuildAllUnitsAndPlayerSandbox()
        {
            BuildSingleBuildWithAllUnits();
            BuildPlayerSandbox();
        }

        [MenuItem("Build/BuildPlayer Sandbox")]
        public static void BuildPlayerSandbox()
        {
            BuildFromProfile("Player Sandbox");
        }
        #region  Separate Dialouge Audio Resources 

        [MenuItem("Build/Separate Dialogue Audio/Build All Units with Separate Dialogue Resources")]
        public static void BuildAllBuildsSeparateDialogueAudio()
        {
            BuildFromProfile(Unit1SeparateAudioResources);
            BuildFromProfile(Unit2SeparateAudioResources);
            BuildFromProfile(Unit3SeparateAudioResources);
            BuildFromProfile(Unit4SeparateAudioResources);
            BuildFromProfile(Unit5SeparateAudioResources);
            BuildFromProfile(MHSUnitLoader);
        }



        [MenuItem("Build/Separate Dialogue Audio/Build Unit 1")]
        public static void BuildUnit1SeparateDialogueAudio()
        {
            BuildFromProfile(Unit1SeparateAudioResources);
            BuildFromProfile(MHSUnitLoader);
        }

        [MenuItem("Build/Separate Dialogue Audio/Build Unit 2")]
        public static void BuildUnit2SeparateDialogueAudio()
        {
            BuildFromProfile(Unit2SeparateAudioResources);
        }

        [MenuItem("Build/Separate Dialogue Audio/Build Unit 3")]
        public static void BuildUnit23eparateDialogueAudio()
        {
            BuildFromProfile(Unit3SeparateAudioResources);
        }

        [MenuItem("Build/Separate Dialogue Audio/Build Unit 4")]
        public static void BuildUnit4SeparateDialogueAudio()
        {
            BuildFromProfile(Unit4SeparateAudioResources);
        }

        [MenuItem("Build/Separate Dialogue Audio/Build Unit 5")]
        public static void BuildUnit5SeparateDialogueAudio()
        {
            BuildFromProfile(Unit5SeparateAudioResources);
        }

        [MenuItem("Build/Separate Dialogue Audio/Build Units 1 and 2")]
        public static void BuildUnits1And2SeparateDialogueAudio()
        {

            BuildFromProfile(Unit1SeparateAudioResources);
            BuildFromProfile(Unit2SeparateAudioResources);
            BuildFromProfile(MHSUnitLoader);
        }

        [MenuItem("Build/Separate Dialogue Audio/MHS Unit Loader")]
        public static void BuildMHSUnitLoader()
        {
            BuildFromProfile(MHSUnitLoader);
        }

        #endregion

        [MenuItem("Build/Build All Profiles")]
        public static void BuildAllProfiles()
        {
            if (Application.isBatchMode)
            {
                AssetDatabase.Refresh();

            }
            foreach (var profile in GetAllBuildProfiles())
            {
                BuildFromProfile(profile);
            }
        }

        public static void BuildFromProfile(BuildProfile profile)
        {
            string formattedProfileName = profile.name.Replace(" ", "");

            //Find or create parent build directory associated with this version
            string parentDirectory = $"{DateTime.Now.ToString("yyyyMMdd")}-{GetChangeListNumber()}";
            if (!Directory.Exists(BuildPathBaseDirectory + "/" + parentDirectory))
            {
                Directory.CreateDirectory(BuildPathBaseDirectory + "/" + parentDirectory);
            }

            // string folderName = formattedProfileName;
            string folderName = GetUnitNameFromBuildProfile(profile.name);



            BuildProfile.SetActiveBuildProfile(profile);
            var options = new BuildPlayerWithProfileOptions
            {
                buildProfile = profile,
                locationPathName = BuildPathBaseDirectory + "/" + parentDirectory + "/" + folderName,
            };

            _buildPath = options.locationPathName;

            var startTime = System.DateTime.Now;
            BuildPipeline.BuildPlayer(options);
            var endTime = System.DateTime.Now;



            Debug.Log($"WebGL build completed in {(endTime - startTime).TotalSeconds} seconds.");
            Debug.Log($"Build output located at: {_buildPath}");
        }

        public static void BuildFromProfile(string profileName)
        {
            BuildFromProfile(GetProfileFromName(profileName));
        }

        #region Addressable Functions

        //This entire section is copied from the Addressables Documentation: https://docs.unity3d.com/Packages/com.unity.addressables@2.7/manual/AddressableAssetSettings.html#build
        static void getSettingsObject(string settingsAsset)
        {
            // This step is optional, you can also use the default settings:
            //settings = AddressableAssetSettingsDefaultObject.Settings;

            settings
                = AssetDatabase.LoadAssetAtPath<ScriptableObject>(settingsAsset)
                    as AddressableAssetSettings;

            if (settings == null)
                Debug.LogError($"{settingsAsset} couldn't be found or isn't " +
                               $"a settings object.");
        }



        static void setProfile(string profile)
        {
            string profileId = settings.profileSettings.GetProfileId(profile);
            if (string.IsNullOrEmpty(profileId))
                Debug.LogWarning($"Couldn't find a profile named, {profile}, " +
                                 $"using current profile instead.");
            else
                settings.activeProfileId = profileId;
        }



        static void setBuilder(IDataBuilder builder)
        {
            int index = settings.DataBuilders.IndexOf((ScriptableObject)builder);

            if (index > 0)
                settings.ActivePlayerDataBuilderIndex = index;
            else
                Debug.LogWarning($"{builder} must be added to the " +
                                 $"DataBuilders list before it can be made " +
                                 $"active. Using last run builder instead.");
        }



        static bool buildAddressableContent()
        {
            AddressableAssetSettings
                .BuildPlayerContent(out AddressablesPlayerBuildResult result);
            bool success = string.IsNullOrEmpty(result.Error);

            if (!success)
            {
                Debug.LogError("Addressables build error encountered: " + result.Error);
            }

            return success;
        }

        [MenuItem("Build/Addressables/Build Addressables Content only")]
        public static bool BuildAddressables()
        {
            getSettingsObject(settings_asset);
            setProfile(profile_name);
            IDataBuilder builderScript
                = AssetDatabase.LoadAssetAtPath<ScriptableObject>(build_script) as IDataBuilder;

            if (builderScript == null)
            {
                Debug.LogError(build_script + " couldn't be found or isn't a build script.");
                return false;
            }

            setBuilder(builderScript);

            return buildAddressableContent();
        }

        [MenuItem("Build/Addressables/Build Addressables Content and Player")]
        public static void BuildAddressablesAndPlayer()
        {
            bool contentBuildSucceeded = BuildAddressables();

            if (contentBuildSucceeded)
            {
                BuildFromProfile("Addressables");
                /*var options = new BuildPlayerOptions();
                BuildPlayerOptions playerSettings
                    = BuildPlayerWindow.DefaultBuildMethods.GetBuildPlayerOptions(options);

                BuildPipeline.BuildPlayer(playerSettings);*/
            }
        }
        #endregion

        public static BuildProfile GetProfileFromName(string profileName)
        {
            string[] guids = AssetDatabase.FindAssets("t:buildprofile", new[] { "Assets/Settings/Build Profiles" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
                if (profile.name == profileName)
                {
                    return profile;
                }
            }
            return null;
        }

        public static List<BuildProfile> GetAllBuildProfiles()
        {
            List<BuildProfile> buildProfiles = new List<BuildProfile>();
            string[] guids = AssetDatabase.FindAssets("t:buildprofile", new[] { "Assets/Settings/Build Profiles" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
                buildProfiles.Add(profile);
            }
            return buildProfiles;
        }

        #region  Post Process Build
        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                // Prefer the actual output path the build pipeline used. This is
                // populated regardless of how the build was launched (project menu,
                // File > Build, build-profile UI). The static _buildPath is only
                // set when launching via BuildFromProfile().
                if (report != null && !string.IsNullOrEmpty(report.summary.outputPath))
                {
                    _buildPath = report.summary.outputPath;
                }
                else if (string.IsNullOrEmpty(_buildPath))
                {
                    _buildPath = BuildPathBaseDirectory;
                }

                //Create Build Report Directory if it doesn't exist
                if (!Directory.Exists(k_BuildReportDirectory))
                {
                    Directory.CreateDirectory(k_BuildReportDirectory);
                }

                // Use the report's own timestamp when available. Falling back to
                // File.GetLastWriteTime on a file that may not exist yields a
                // sentinel (FILETIME 0 -> 1600-12-31 local time on macOS) and
                // produces filenames like "BuildReport_1600 -31-12-16-07-00".
                var date = DateTime.Now;
                if (report != null && report.summary.buildEndedAt != default)
                {
                    date = report.summary.buildEndedAt;
                }
                else if (File.Exists(k_LastBuildReportFileName))
                {
                    date = File.GetLastWriteTime(k_LastBuildReportFileName);
                }

                var reportFileName = "BuildReport_" + GetChangeListNumber() + date.ToString("yyyy -dd-MM-HH-mm-ss") + ".buildreport";
                var textFileName = "BuildReport_" + GetChangeListNumber() + date.ToString("yyyy -dd-MM-HH-mm-ss") + ".txt";

                //Export the assets in this buld report as CSV
                string reportCSV = ExportBuildReportCSV(report, reportFileName);

                if (string.IsNullOrEmpty(reportCSV))
                {
                    Debug.Log($"Export complete at {k_BuildReportDirectory}/{reportFileName}");
                }
                else
                {
                    Debug.LogError(reportCSV);
                }

                //Write relevent parts of Build Report to a text file.  Place in both the Build Report Directory
                //and the folder the build files are in.

                string buildReportTextFile = WriteBuildReportToTextFile(report, k_BuildReportDirectory);

                if (string.IsNullOrEmpty(buildReportTextFile))
                {
                    return;
                }

                // Make sure the build output directory exists before copying. If
                // the build didn't produce output (e.g. early failure) or the
                // path is unexpected, skip rather than throw.
                if (!Directory.Exists(_buildPath))
                {
                    Debug.LogWarning($"Build output directory '{_buildPath}' does not exist; skipping copy of build report text file to the build folder.");
                    return;
                }

                var buildFolderDestination = Path.Combine(_buildPath, textFileName);

                if (!File.Exists(buildFolderDestination))
                {
                    File.Copy(buildReportTextFile, buildFolderDestination, true);
                }
            }
            catch (Exception ex)
            {
                // LogWarning, not LogError: post-process reporting failures must
                // never flip an otherwise-successful build to "Failed".
                Debug.LogWarning($"OnPostprocessBuild error (non-fatal): {ex}");
            }
        }

        #endregion

        #region Build Report Functions

        [MenuItem("Build/Reporting/GetLatestBuildReportAsCSV")]
        public static void SaveLastBuildReportAsCSV()
        {
            if (!File.Exists(k_LastBuildReportFileName))
            {
                Debug.LogError($"No build report exists at {k_LastBuildReportFileName}");
                return;
            }
            else
            {
                if (!Directory.Exists(k_BuildReportDirectory))
                {
                    Directory.CreateDirectory(k_BuildReportDirectory);
                }
                var date = File.GetLastWriteTime(k_LastBuildReportFileName);
                var reportFileName = "BuildReport_" + GetChangeListNumber() + date.ToString("yyyy -dd-MM-HH-mm-ss") + ".buildreport";

                //Copy file to local assets folder
                var projectDestination = $"{k_BuildReportDirectory}/{reportFileName}";
                if (!File.Exists(projectDestination))
                {
                    var tempPath = k_BuildReportDirectory + "/LastBuild.buildreport";
                    File.Copy(k_LastBuildReportFileName, tempPath, true);
                    AssetDatabase.ImportAsset(tempPath);
                    AssetDatabase.RenameAsset(tempPath, reportFileName);
                }
            }
        }

        public static string ExportBuildReportCSV(BuildReport buildReport, string reportFileName)
        {
            try
            {
                if (buildReport == null)
                    return "ExportBuildReportCSV: buildReport is null; skipping.";

                //Create Content Analysis
                ContentAnalysis contentAnalysis = new ContentAnalysis(buildReport, 10000, true);
                Debug.Log($"Build Path is {_buildPath}");
                if (string.IsNullOrEmpty(_buildPath))
                {
                    _buildPath = !string.IsNullOrEmpty(buildReport.summary.outputPath)
                        ? buildReport.summary.outputPath
                        : BuildPathBaseDirectory;
                    Debug.Log($"Build path adjusted to {_buildPath}");
                }

                // Ensure destination directory exists before SaveAssetsToCsv writes.
                if (!Directory.Exists(_buildPath))
                {
                    Directory.CreateDirectory(_buildPath);
                }

                string exportPath = $"{_buildPath}/{reportFileName}";
                exportPath = Path.ChangeExtension(exportPath, null) + "_SourceAssets.csv";
                return contentAnalysis.SaveAssetsToCsv(exportPath);
            }
            catch (Exception ex)
            {
                return $"ExportBuildReportCSV failed: {ex.Message}";
            }
        }

        // [MenuItem("Builds/Reporting/Write to File Test")]
        public static string WriteBuildReportToTextFile(BuildReport buildReport, string docPath)
        {
            if (buildReport == null)
            {
                Debug.LogError("No build report found");
                return "";
            }

            if (string.IsNullOrEmpty(docPath))
            {
                docPath = k_BuildReportDirectory;
            }

            PackedAssets[] packedAssets = buildReport.packedAssets;

            string reportFilePath = Path.Combine(docPath, $"{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")}BuildReport.txt");

            using (StreamWriter outputfile = new StreamWriter(reportFilePath))
            {
                outputfile.WriteLine($"Result {buildReport.summary.result}");

                outputfile.WriteLine($"Size: {buildReport.summary.totalSize / 1000} KB ");
                outputfile.WriteLine($"Platform: {buildReport.summary.platform}");
                outputfile.WriteLine($"Path {buildReport.summary.outputPath}");
                outputfile.WriteLine($"Total Time {buildReport.summary.totalTime.Minutes} minutes");
                outputfile.WriteLine($"Total Errors {buildReport.summary.totalErrors}");
                outputfile.WriteLine($"Error Summary: {buildReport.SummarizeErrors()}");

                outputfile.WriteLine("\nPACKED ASSETS\n");


                foreach (PackedAssets assets in packedAssets)
                {
                    outputfile.WriteLine($"{assets.name}, {assets.shortPath}, {assets.overhead}, {assets.contents.Length}");
                    foreach (PackedAssetInfo packedAssetInfo in assets.contents)
                    {
                        outputfile.WriteLine($" - ID {packedAssetInfo.id}, {packedAssetInfo.sourceAssetPath},  {packedAssetInfo.packedSize / 1000} KB    {packedAssetInfo.type}");
                    }
                }

                outputfile.WriteLine("\nSTRIPPING INFO\n");

                foreach (string includedModule in buildReport.strippingInfo.includedModules)
                {
                    foreach (string reason in buildReport.strippingInfo.GetReasonsForIncluding(includedModule))
                    {
                        outputfile.WriteLine($"Included Module: {includedModule} for {reason}");
                    }
                }

                outputfile.WriteLine("\nBUILD STEPS\n");

                foreach (BuildStep buildStep in buildReport.steps)
                {
                    outputfile.WriteLine($" {buildStep.duration.TotalSeconds} seconds, ");
                    foreach (BuildStepMessage buildStepMessage in buildStep.messages)
                    {
                        outputfile.WriteLine($"{buildStepMessage.type}, {buildStepMessage.content}");
                    }

                }

            }
            return reportFilePath;
        }
        #endregion

        # region Naming Utility Functions

        public static string GetUnitNameFromBuildProfile(string profileName)
        {
            string unitName = "";
            if (profileName.Contains("1"))
            {
                unitName = "unit1";
            }

            if (profileName.Contains("2"))
            {
                unitName = "unit2";
            }
            if (profileName.Contains("3"))
            {
                unitName = "unit3";
            }

            if (profileName.Contains("4"))
            {
                unitName = "unit4";
            }

            if (profileName.Contains("5"))
            {
                unitName = "unit5";
            }

            if (profileName.Contains("Loader"))
            {
                unitName = "loader";
            }

            return unitName;
        }

        public static string GetChangeListNumber()
        {
            string changeListNumber = "";
            if (Application.isBatchMode)
            {
                var args = System.Environment.GetCommandLineArgs();
                Debug.Log("Getting Command Line Args");
                if (args == null)
                {
                    Debug.Log("No command line args. Returning 0000 for changelist num");
                    changeListNumber = "0000";
                    return changeListNumber;
                }

                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-changelistNum" && args.Length > i + 1)
                    {
                        Debug.Log($"Argument is {i}");
                        changeListNumber = args[i + 1];
                    }
                }
            }
            else
            {

                changeListNumber = ChangelistTest.changeListNumber;

            }

            return changeListNumber;
        }

        #endregion
    }
}
