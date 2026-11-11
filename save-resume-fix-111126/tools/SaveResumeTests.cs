// SaveResumeTests: Editor tests for the save/resume fixes (mhs-updates save-resume-fix-111126).
//
// Each scene test runs in its own play session: it puts the unit's scene in the build scene list (the game decides its
// unit from that list, SceneLoadingUtilities.GetUnitNumberForThisBuild, and play mode reads the list when it starts),
// enters play mode, loads the scene, applies a game save from Fixtures/ through CheckpointSaver.ApplySaveCheckpointFirst
// (what DatabaseSaver calls), lets the game finish its resume, checks what the fix guarantees, and leaves play mode.
// The build scene list is put back after each test. The fixtures are game saves (quest states, variables, saver data);
// they hold no user id, name or typed text.
//
// These are Edit Mode tests (Test Runner, EditMode tab) because each needs its own play session.
// Install: Assets/Tests/SaveResume/Editor/SaveResumeTests.cs and Assets/Tests/SaveResume/Fixtures/*.json
// (see tools/README.md).
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Obvious.Soap;
using PixelCrushers;
using PixelCrushers.DialogueSystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MHS.SaveResumeTests
{
    public class SaveResumeTests
    {
        const string SavedScenesKey = "MHS.SaveResumeTests.BuildScenes";

        // Each test runs in its own play session with only its unit's scene in the build list, so the game sees that unit.
        // The unit comes from the test's name (_Unit3_, _Unit4_; Unit 2 otherwise).
        static string SceneFor(string testName) =>
            testName.Contains("_Unit3_") ? Unit3Scene : testName.Contains("_Unit4_") ? Unit4Scene : Unit2Scene;

        const string CheckedKey = "MHS.SaveResumeTests.Checked";

        [UnitySetUp]
        public IEnumerator EnterThePlaySessionOfTheTestsUnit()
        {
            // A session left over from a failed test is left first: every test gets a fresh one.
            if (Application.isPlaying) { LogAssert.ignoreFailingMessages = true; yield return new ExitPlayMode(); }
            SessionState.SetBool(CheckedKey, false);
            if (string.IsNullOrEmpty(SessionState.GetString(SavedScenesKey, "")))
                SessionState.SetString(SavedScenesKey, string.Join("\n", EditorBuildSettings.scenes.Select(x => (x.enabled ? "1" : "0") + x.path)) + "\n");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(SceneFor(TestContext.CurrentContext.Test.MethodName), true) };
            yield return new EnterPlayMode();
        }

        // Called after a test's last check. If play mode ends while a test is still running, Unity can report the cut-short
        // test as passed; the tear-down fails any "passed" test that did not get here.
        static void Checked() => SessionState.SetBool(CheckedKey, true);

        [UnityTearDown]
        public IEnumerator End()
        {
            // Leaving play mode stops the scene's timelines; one of the game's timeline triggers then logs a
            // MissingReferenceException (it has already been destroyed). Not the fix's doing; let it through here too.
            LogAssert.ignoreFailingMessages = true;
            bool passedWithoutItsChecks = TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed
                                          && !SessionState.GetBool(CheckedKey, false);
            if (Application.isPlaying) yield return new ExitPlayMode();
            var saved = SessionState.GetString(SavedScenesKey, "");
            if (!string.IsNullOrEmpty(saved))
            {
                EditorBuildSettings.scenes = saved.Split('\n').Where(l => l.Length > 1)
                    .Select(l => new EditorBuildSettingsScene(l.Substring(1), l[0] == '1')).ToArray();
                SessionState.EraseString(SavedScenesKey);
            }
            Assert.IsFalse(passedWithoutItsChecks, "the test ended before its checks ran (play mode ended during the test); run it again");
        }
        const string Unit2Scene = "Assets/Scenes/Gameplay/Unit 2 Prod/Unit 2 Prod (Refactor).unity";
        const string Unit3Scene = "Assets/Scenes/Gameplay/Unit 3/Unit 3 Dev.unity";
        const string Unit4Scene = "Assets/Scenes/Gameplay/Unit 4/Unit 4 Dev.unity";


        // ---------------------------------------------------------------- the checkpoint saver applies first

        // Unit 3: the save records Supply Crate 1 shown; checkpoint replay event 1 hides the Supply Run objects. With the
        // checkpoint saver applied last (the order that hid the crate before the fix) the crate must still come back shown.
        [UnityTest]
        public IEnumerator T01_Unit3_SupplyCrate_ComesBackAsSaved_WhateverTheSaverOrder()
        {
            yield return Resume(Unit3Scene, "u3-ck5.json", checkpointLastInSet: true);
            var crate = Find("===== Quest Objects ========/Quest1-Supply Run (1)/Supply Crate 1/Mesh_Tera'sNEWStorageBoxes_SupplyRunQuest");
            Assert.IsNotNull(crate, "Supply Crate 1 mesh not found");
            Assert.IsTrue(crate.activeInHierarchy, "Supply Crate 1 is hidden, but the save records it shown");
            Checked();
        }

        // Unit 4: the save records the Infiltration glyph's collider off; checkpoint replay event 2 turns it on.
        [UnityTest]
        public IEnumerator T02_Unit4_GlyphCollider_ComesBackAsSaved_WhateverTheSaverOrder()
        {
            yield return Resume(Unit4Scene, "u4-ck2.json", checkpointLastInSet: true);
            var glyph = Find("===== Quest Objects ========/GlyphGame_Infiltration");
            Assert.IsNotNull(glyph, "GlyphGame_Infiltration not found");
            var col = glyph.GetComponent<BoxCollider>();
            Assert.IsNotNull(col);
            Assert.IsFalse(col.enabled, "the Infiltration glyph's collider is on, but the save records it off");
            Checked();
        }

        // The checkpoint replay runs once per resume: SaveSystem's own pass skips the checkpoint saver that
        // ApplySaveCheckpointFirst already applied, and a second apply in the same scene replays again.
        [UnityTest]
        public IEnumerator T03_Unit2_CheckpointReplay_RunsOncePerApply()
        {
            int replays = 0;
            yield return Resume(Unit2Scene, "u2-ck4.json", beforeApply: () =>
            {
                foreach (var cs in UnityEngine.Object.FindObjectsByType<CheckpointSaver>(FindObjectsSortMode.None))
                {
                    var events = (List<UnityEngine.Events.UnityEvent>)typeof(CheckpointSaver)
                        .GetField("events", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cs);
                    events[0].AddListener(() => replays++);
                }
            });
            Assert.AreEqual(1, replays, "replay event 0 should run once for one apply");
            CheckpointSaver.ApplySaveCheckpointFirst(Load("u2-ck4.json"));
            Assert.AreEqual(2, replays, "a second apply in the same scene should replay again (no stale skip flag)");
            Checked();
        }

        // Checkpoint 14 (Claim of Command, step 1 "Check on Jasper"): after a resume the quest and its step are active,
        // and step 1's own setup has run as in live play: Jasper at the campsite is talked to through his
        // Post-Argumentation object, not his main Usable.
        [UnityTest]
        public IEnumerator T05_Unit2_Checkpoint14_ResumesWithTheFinalQuestActive()
        {
            yield return Resume(Unit2Scene, "u2-ck14.json");
            Assert.AreEqual(QuestState.Active, QuestLog.GetQuestState("Claim of Command"), "Claim of Command");
            Assert.AreEqual(QuestState.Active, QuestLog.GetQuestEntryState("Claim of Command", 1), "Claim of Command step 1");
            Assert.IsFalse(Usable("===== NPCs =============/Jasper-Unit2 Campsite"), "Jasper's main Usable should be off at step 1");
            var post = Find("===== NPCs =============/Jasper-Unit2 Campsite/Post-Argumentation");
            Assert.IsNotNull(post, "Post-Argumentation not found");
            Assert.IsTrue(post.activeInHierarchy, "Jasper's Post-Argumentation should be active at step 1");
            Checked();
        }

        // A side quest the student had started stays started after a Unit 2 resume.
        [UnityTest]
        public IEnumerator T06_Unit2_SideQuest_StaysStartedAfterAResume()
        {
            yield return Resume(Unit2Scene, "u2-ck12.json");
            Assert.AreEqual(QuestState.Active, QuestLog.GetQuestState("Creating a Catalogue"), "Creating a Catalogue");
            Checked();
        }

        // ---------------------------------------------------------------- fog of war is set, not toggled

        // The flag must say whether the fog switcher's image is shown (before the fix it was toggled and could say the opposite).
        [UnityTest]
        public IEnumerator T07_Unit2_FogOfWarFlag_MatchesTheFogImage_AfterAResume()
        {
            yield return Resume(Unit2Scene, "u2-ck7.json");
            var fow = Find("===== UI Systems========/UIObjectSwitcher_Gameplay_Unit2/Canvas_Gameplay_Map_Unit2_New/Container/Panel: Map Image/Image: Fog of War");
            Assert.IsNotNull(fow, "the map's Image: Fog of War not found");
            Assert.IsNotNull(fow.GetComponent<FOWMapSwitcher>(), "Image: Fog of War has no FOWMapSwitcher");
            var flag = Var<BoolVariable>("Assets/Data/Scriptables/Map/BoolVariable_WorldSpaceMap_FOWIsActive.asset");
            Assert.AreEqual(fow.GetComponent<UnityEngine.UI.Image>().enabled, flag.Value, "FOWIsActive should say whether the fog image is shown");
            Checked();
        }

        // ---------------------------------------------------------------- map hint panel and text

        [UnityTest]
        public IEnumerator T08_Unit2_Checkpoint4_KeepsTheMapHintText()
        {
            yield return Resume(Unit2Scene, "u2-ck4.json");
            Assert.IsTrue(Var<BoolVariable>("Assets/Data/Scriptables/Map/Map Hints/BoolVariable_MapHint_HintTextOn.asset").Value, "HintTextOn");
            Assert.IsTrue(Var<BoolVariable>("Assets/Data/Scriptables/Map/Map Hints/BoolVariable_MapHint_PanelOn.asset").Value, "PanelOn (step 2 is active)");
            Checked();
        }

        [UnityTest]
        public IEnumerator T09_Unit2_Checkpoint6_MapHintPanelIsOff()
        {
            yield return Resume(Unit2Scene, "u2-ck6.json");
            Assert.IsFalse(Var<BoolVariable>("Assets/Data/Scriptables/Map/Map Hints/BoolVariable_MapHint_PanelOn.asset").Value, "PanelOn");
            Assert.IsTrue(Var<BoolVariable>("Assets/Data/Scriptables/Map/Map Hints/BoolVariable_MapHint_HintTextOn.asset").Value, "HintTextOn");
            Checked();
        }

        // ---------------------------------------------------------------- the five new Unit 2 save points

        // From the save before it, starting the step makes the game save with that checkpoint number.
        [UnityTest] public IEnumerator T10_Unit2_SavesAtCheckpoint2() { var r = new Saves(); yield return SavesAt("u2-ck1.json", "Foraged Forging", 2, new[] { "Q|Foraged Forging|1|success" }, r); r.Check("Foraged Forging", 2, 2); Checked(); }
        [UnityTest] public IEnumerator T11_Unit2_SavesAtCheckpoint3() { var r = new Saves(); yield return SavesAt("u2-ck2.json", "Getting the Band Back Together", 1, new[] { "Q|Foraged Forging|2|success", "Q|Foraged Forging|3|active", "Q|Foraged Forging|3|success", "QS|Foraged Forging|success", "QS|Getting the Band Back Together|active" }, r); r.Check("Getting the Band Back Together", 1, 3); Checked(); }
        [UnityTest] public IEnumerator T12_Unit2_SavesAtCheckpoint5() { var r = new Saves(); yield return SavesAt("u2-ck4.json", "Getting the Band Back Together", 3, new[] { "Q|Getting the Band Back Together|2|success" }, r); r.Check("Getting the Band Back Together", 3, 5); Checked(); }
        [UnityTest] public IEnumerator T13_Unit2_SavesAtCheckpoint9() { var r = new Saves(); yield return SavesAt("u2-ck8.json", "Which Watershed?", 1, new[] { "Q|Getting the Band Back Together|5|success", "QS|Getting the Band Back Together|success", "QS|Which Watershed?|active" }, r); r.Check("Which Watershed?", 1, 9); Checked(); }
        [UnityTest] public IEnumerator T14_Unit2_SavesAtCheckpoint11() { var r = new Saves(); yield return SavesAt("u2-ck10.json", "Which Watershed?", 5, new[] { "Q|Which Watershed?|2|success", "Q|Which Watershed?|3|active", "Q|Which Watershed?|3|success", "Q|Which Watershed?|4|active", "Q|Which Watershed?|4|success" }, r); r.Check("Which Watershed?", 5, 11); Checked(); }

        // A save made at a new save point resumes at that checkpoint with its step active.
        [UnityTest] public IEnumerator T15_Unit2_ResumesAtCheckpoint2() { yield return Resume(Unit2Scene, "u2-ck2.json"); CheckResumedAt(2, "Foraged Forging", 2); Checked(); }
        [UnityTest] public IEnumerator T16_Unit2_ResumesAtCheckpoint3() { yield return Resume(Unit2Scene, "u2-ck3.json"); CheckResumedAt(3, "Getting the Band Back Together", 1); Checked(); }
        [UnityTest] public IEnumerator T17_Unit2_ResumesAtCheckpoint5() { yield return Resume(Unit2Scene, "u2-ck5.json"); CheckResumedAt(5, "Getting the Band Back Together", 3); Checked(); }
        [UnityTest] public IEnumerator T18_Unit2_ResumesAtCheckpoint9() { yield return Resume(Unit2Scene, "u2-ck9.json"); CheckResumedAt(9, "Which Watershed?", 1); Checked(); }
        [UnityTest] public IEnumerator T19_Unit2_ResumesAtCheckpoint11() { yield return Resume(Unit2Scene, "u2-ck11.json"); CheckResumedAt(11, "Which Watershed?", 5); Checked(); }

        // The saves the game raised: before the step, and when the step started. Checked in the test body: a failed check
        // inside a nested coroutine leaves Unity's Edit Mode runner unable to give the next test a clean play session.
        class Saves
        {
            public readonly List<int> Before = new List<int>(), AtStep = new List<int>();
            public void Check(string quest, int entry, int checkpoint)
            {
                Assert.IsEmpty(Before, "a save was raised before the step");
                Assert.AreEqual(new List<int> { checkpoint }, AtStep, $"starting {quest} step {entry} should save once, at checkpoint {checkpoint}");
            }
        }

        // Resume the save before the new save point, play the steps up to it, start the step, and record the saves raised.
        static IEnumerator SavesAt(string fixture, string quest, int entry, string[] stepsBefore, Saves r)
        {
            yield return Resume(Unit2Scene, fixture);
            var ev = SaveEvent();
            if (ev == null) yield break; // r stays empty: Check fails on it
            var current = r.Before;
            Action<int> onSave = _ => current.Add(CheckpointNow());
            ev.OnRaised += onSave;
            foreach (var s in stepsBefore) { Step(s); yield return null; }
            current = r.AtStep;
            QuestLog.SetQuestEntryState(quest, entry, QuestState.Active);
            for (int i = 0; i < 5; i++) yield return null;
            ev.OnRaised -= onSave;
        }

        static void CheckResumedAt(int checkpoint, string quest, int entry)
        {
            Assert.AreEqual(checkpoint, CheckpointNow(), "checkpoint after the resume");
            Assert.AreEqual(QuestState.Active, QuestLog.GetQuestEntryState(quest, entry), $"{quest} step {entry}");
        }

        // ---------------------------------------------------------------- helpers

        static string FixtureDir => Path.Combine(Application.dataPath, "Tests/SaveResume/Fixtures");
        static SavedGameData Load(string fixture) => JsonUtility.FromJson<SavedGameData>(File.ReadAllText(Path.Combine(FixtureDir, fixture)));

        // Load the scene with the unit's own build scene list in place (the game decides its unit from that list; the
        // Editor's list holds every unit, which makes the game think it is Unit 1), apply the save as DatabaseSaver
        // does (end of the first frame), and let the resume finish.
        static IEnumerator Resume(string scenePath, string fixture, bool checkpointLastInSet = false, Action beforeApply = null)
        {
            // A unit scene started on its own (no StrataHub page, no main menu, no services) logs errors and exceptions of
            // its own while it starts (e.g. DisableForReleaseBuild, DoorController). Unity's test framework fails a test on
            // any logged error, so the tests let them through and check the state instead.
            LogAssert.ignoreFailingMessages = true;
            var saved = Load(fixture);
            var op = EditorSceneManager.LoadSceneAsyncInPlayMode(scenePath, new LoadSceneParameters(LoadSceneMode.Single));
            while (!op.isDone) yield return null;
            yield return null;
            // The game must see the scene's own unit; otherwise it resets that unit's quests on resume.
            int expected = int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(scenePath), @"Unit (\d)").Groups[1].Value);
            Assert.AreEqual(expected, SceneLoadingUtilities.GetUnitNumberForThisBuild(), "the unit the game sees for " + scenePath);
            if (checkpointLastInSet) PutCheckpointSaverLastInTheSaveSystem();
            beforeApply?.Invoke();
            CheckpointSaver.ApplySaveCheckpointFirst(saved);
            yield return null;
            // CheckpointSaver finishes its resume at the end of the frame (WaitForEndOfFrame). Unity's batch mode
            // never signals end of frame, so there (and only there) the test runs that step itself.
            if (Application.isBatchMode) RunCheckpointEndOfFrameStep();
            for (float t = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t < 3f;) yield return null;
        }

        // SaveSystem applies its savers from the end of a copy of its set: rebuilding the set with the checkpoint saver
        // inserted first makes the library apply it last, the order that broke the Unit 3 crate before the fix.
        static void PutCheckpointSaverLastInTheSaveSystem()
        {
            var f = typeof(SaveSystem).GetField("m_savers", BindingFlags.NonPublic | BindingFlags.Static);
            var all = ((HashSet<Saver>)f.GetValue(null)).Where(s => s != null).ToList();
            var set = new HashSet<Saver>();
            foreach (var s in all.Where(s => s is CheckpointSaver)) set.Add(s);
            foreach (var s in all.Where(s => !(s is CheckpointSaver))) set.Add(s);
            f.SetValue(null, set);
        }

        static void RunCheckpointEndOfFrameStep()
        {
            var m = typeof(CheckpointSaver).GetMethod("ApplyWaypoints", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) return;
            foreach (var cs in UnityEngine.Object.FindObjectsByType<CheckpointSaver>(FindObjectsSortMode.None))
            {
                var r = m.Invoke(cs, null);
                if (r is IEnumerator it) while (it.MoveNext()) { }
            }
        }

        static GameObject Find(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                var first = path.Split('/')[0];
                foreach (var root in sc.GetRootGameObjects())
                {
                    if (root.name != first) continue;
                    var rest = path.Substring(first.Length).TrimStart('/');
                    var t = rest.Length == 0 ? root.transform : root.transform.Find(rest);
                    if (t != null) return t.gameObject;
                }
            }
            return null;
        }

        static bool Usable(string path)
        {
            var go = Find(path);
            Assert.IsNotNull(go, path + " not found");
            var u = go.GetComponent<Usable>();
            Assert.IsNotNull(u, path + " has no Usable");
            return u.enabled;
        }

        static T Var<T>(string assetPath) where T : UnityEngine.Object
        {
            var v = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            Assert.IsNotNull(v, assetPath + " not found");
            return v;
        }

        static int CheckpointNow()
        {
            var cs = UnityEngine.Object.FindAnyObjectByType<CheckpointSaver>();
            var f = typeof(CheckpointSaver).GetField("sceneCheckpoint", BindingFlags.NonPublic | BindingFlags.Instance);
            return ((IntVariable)f.GetValue(cs)).Value;
        }

        static ScriptableEventInt SaveEvent()
        {
            var f = typeof(QuestEventValueListener).GetField("_SaveGameEvent", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var l in UnityEngine.Object.FindObjectsByType<QuestEventValueListener>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (f.GetValue(l) is ScriptableEventInt e && e != null) return e;
            return null;
        }

        // "Q|quest|entry|state" or "QS|quest|state"
        static void Step(string s)
        {
            var p = s.Split('|');
            if (p[0] == "Q") QuestLog.SetQuestEntryState(p[1], int.Parse(p[2]), QuestLog.StringToState(p[3]));
            else QuestLog.SetQuestState(p[1], QuestLog.StringToState(p[2]));
        }
    }

    // The quest table: no play session needed.
    public class QuestTableTests
    {
        static readonly string[] Unit2Quests = { "Claim of Command", "A Word from Our Sponsors", "Secrets of the Lost Society", "The Measure of a Mutt", "Creating a Catalogue" };

        [Test]
        public void T04_QuestTable_ListsTheUnit2QuestsUnderUnit2()
        {
            var table = AssetDatabase.LoadAssetAtPath<QuestAndUnitSO>("Assets/Data/Dialogue System/QuestAndUnitSO.asset");
            Assert.IsNotNull(table, "QuestAndUnitSO.asset not found");
            foreach (var q in Unit2Quests)
                Assert.AreEqual(Unit.Unit2, QuestResetUtility.GetUnitNumberFromQuest(q, table.QuestsByUnit), q);
        }

    }
}
#endif
