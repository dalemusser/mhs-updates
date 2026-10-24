#if UNITY_EDITOR
// PlayMode repro for the Unit 4 "memory access out of bounds" crash at Tera's garden boxes.
// See mhs-updates/unit4-gardenbox-crash-102326/02-hypothesis-and-plan.md.
//
// Drives GardenBoxesUIController exactly the way the prefab's buttons and the world boxes'
// usables do, on the real TerasGardenBoxesUI prefab, with stand-ins for what the Unit 4 scene
// supplies (object switcher, planter usables, colliders, the "Dialogue Canvas"). No dialogue
// manager other than the project's own DialogueSystemController_Gameplay prefab (database, Lua);
// the garden box prefab's dialogue triggers are neutralised so no conversation starts.
//
// Setup and cleanup are inline (not [UnitySetUp]/[UnityTearDown]): in this project those made the
// editor's test runner overflow its stack before the setup body ran (Unity 6000.0.74f1, UTF 1.3.9).
//
// Run headless:
//   Unity -batchmode -projectPath <project> -runTests -testPlatform PlayMode \
//         -testFilter MHS.GardenBoxCrashRepro -testResults results.xml -logFile unity.log
// (needs ProjectSettings.asset playModeTestRunnerEnabled: 1 and Assets/csc.rsp referencing
//  Library/ScriptAssemblies/UnityEngine.TestRunner.dll so Assembly-CSharp can hold these tests).
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Obvious.Soap;
using PixelCrushers.DialogueSystem;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace MHS.GardenBoxCrashRepro
{
    /// Marks every GameObject a test creates, so the next test can remove leftovers: when the test
    /// framework aborts a test on an unhandled exception log, the test's finally block does not run.
    public class GardenBoxTestObject : MonoBehaviour { }

    public class GardenBoxCrashReproTests
    {
        const string PrefabPath = "Assets/Prefabs/Mini-games/GardenBoxes/TerasGardenBoxesUI.prefab";
        const string DataFolder = "Assets/Data/Mini-games/Garden Boxes/";
        // The scene's dialogue system (database, Lua, "Dialogue Canvas"); the Unit 4 scene has an instance of it.
        const string DialogueSystemPrefabPath = "Assets/Prefabs/_Core/Dialogue Systems/DialogueSystemController_Gameplay.prefab";

        GameObject _root;
        GardenBoxesUIController _ui;
        IntVariable _planterIndex;
        IntVariable[] _soils;
        BoolVariable _finished;
        Canvas _dialogueCanvas;
        Usable[] _usables;
        Button _placeButton;
        Animator[] _flash;
        SOGardenBoxes_PlanterData _data;
        bool[] _introPlayed;
        List<string> _problems;
        List<Object> _extra;

        // ---- the crash (production traces: crash 11 and crash 13) ----

        [UnityTest]
        public IEnumerator A1_ReopeningABoxPlacedWithoutASoil_AfterAllCamerasArePlaced_ClosesWithoutAnException()
        {
            try
            {
                yield return Setup();
                yield return PlaceAllThree(0, -1, 2);       // box 2 placed with no position chosen: soil -1 ("None")
                Assert.AreEqual(-1, _soils[1].Value, "box 2 holds no soil");

                yield return OpenBox(1);                    // re-open the "None" box -> AnimateWateringPlant
                yield return new WaitForSeconds(2.5f);      // the 2 s watering animation, then the indexing
                yield return null;

                Assert.IsEmpty(_problems, Problems());
                Assert.AreEqual(-1, _planterIndex.Value, "CloseUI ran (no dialogue to wait for)");
                Assert.IsFalse(_usables[1].enabled, "the box's usable was turned off");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator A2_ReopeningABoxPlacedWithASoil_AfterAllCamerasArePlaced_FlashesThatPositionAndClosesWhenTheDialogueEnds()
        {
            try
            {
                yield return Setup();
                yield return PlaceAllThree(0, 1, 2);
                _dialogueCanvas.enabled = true;             // the feedback conversation is showing
                yield return OpenBox(2);
                yield return new WaitForSeconds(2.2f);          // the 2 s watering animation

                // The flash is UI_Color_MultiType on the camera image: it fades the image and toggles
                // its GameObject, so sample over a window rather than at one instant.
                bool sawClayFlash = false, sawOtherFlash = false;
                for (float t = 0; t < 1.5f; t += Time.deltaTime)
                {
                    sawClayFlash |= _flash[2].gameObject.activeSelf;
                    sawOtherFlash |= _flash[0].gameObject.activeSelf || _flash[1].gameObject.activeSelf;
                    yield return null;
                }
                Assert.IsEmpty(_problems, Problems());
                Assert.IsTrue(sawClayFlash, "the Clay position's camera image flashes");
                Assert.IsTrue(_flash[2].enabled, "its animator is on while the dialogue shows");
                Assert.IsFalse(sawOtherFlash, "only that position flashes");
                Assert.AreEqual(2, _planterIndex.Value, "the UI stays open while the dialogue shows");
                Assert.IsFalse(_usables[2].enabled, "the box's usable was turned off");

                _dialogueCanvas.enabled = false;            // the conversation ends
                yield return null;
                yield return null;
                Assert.IsFalse(_flash[2].enabled, "the flash stopped");
                Assert.AreEqual(-1, _planterIndex.Value, "CloseUI ran");
                Assert.IsEmpty(_problems, Problems());
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator A3_ClosingTheUIDuringTheWateringAnimation_IsHarmless()
        {
            try
            {
                yield return Setup();
                yield return PlaceAllThree(0, 1, 2);
                yield return OpenBox(0);
                yield return new WaitForSeconds(1f);
                _ui.CloseUI();                              // planter index and soil become -1 mid-animation
                yield return new WaitForSeconds(2f);
                Assert.IsEmpty(_problems, Problems());
            }
            finally { Cleanup(); }
        }

        // ---- option B: the bad state cannot be made ----

        [UnityTest]
        public IEnumerator B1_PlaceCamera_WithoutAPositionChosen_IsRefusedAndTheButtonIsDisabled()
        {
            try
            {
                yield return Setup();
                yield return OpenBox(0);
                Assert.IsFalse(_placeButton.interactable, "Place camera is disabled until a position is chosen");

                _ui.PlaceCamera();                          // what the button's listener would do if it fired anyway
                yield return new WaitForSeconds(0.75f);
                Assert.AreEqual(-1, _soils[0].Value, "nothing was stored");
                Assert.AreEqual(0, _planterIndex.Value, "the UI stayed open");

                ChooseSoil(1);
                Assert.IsTrue(_placeButton.interactable, "Place camera is enabled once a position is chosen");
                yield return PressPlace();
                Assert.AreEqual(1, _soils[0].Value, "the chosen soil was stored");
                Assert.AreEqual(-1, _planterIndex.Value, "the UI closed after placing");
                Assert.IsEmpty(_problems, Problems());
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator B2_ReopeningABoxWithACameraAlreadyPlaced_KeepsPlaceEnabled()
        {
            try
            {
                yield return Setup();
                yield return OpenBox(0); ChooseSoil(0); yield return PressPlace();
                yield return OpenBox(0);                    // re-open before the others are placed
                Assert.IsTrue(_placeButton.interactable, "a box with a camera can be re-placed as before");
                yield return PressPlace();
                Assert.AreEqual(0, _soils[0].Value);
                Assert.IsEmpty(_problems, Problems());
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator B3_WithoutThePrefabReference_TheButtonIsStillFoundAndGated()
        {
            try
            {
                yield return Setup();
                // The prefab edit not applied: the serialized reference is empty.
                var so = new SerializedObject(_ui);
                so.FindProperty("_placeCameraButton").objectReferenceValue = null;
                so.ApplyModifiedPropertiesWithoutUndo();

                yield return OpenBox(0);
                Assert.IsFalse(_placeButton.interactable, "Place camera is disabled until a position is chosen (button found by its listener)");
                ChooseSoil(2);
                Assert.IsTrue(_placeButton.interactable, "enabled once a position is chosen");
                yield return PressPlace();
                Assert.AreEqual(2, _soils[0].Value, "the chosen soil was stored");
                Assert.IsEmpty(_problems, Problems());
            }
            finally { Cleanup(); }
        }

        // ---- the students' steps, as the usables and buttons call them ----

        /// Press E at a box: GardenBoxesPlanter.SetPlanterId then TurnOnUI. The first open of a box
        /// only plays the intro dialogue; its end re-opens the UI (GardenBoxesEventHelper.KeepUIOpen).
        IEnumerator OpenBox(int box)
        {
            Debug.Log($"[GBX] OpenBox({box}) finished={_finished.Value}");
            _ui.SetCurrentPlanterIndex(box);
            _ui.TurnOnUI(_data);
            if (!_introPlayed[box])
            {
                _introPlayed[box] = true;
                _ui.TurnOnUI(_data);
            }
            yield return null;
        }

        /// Click a soil position (the Dirts buttons): SetSoilIndex then PlaceTemporaryCamera.
        void ChooseSoil(int soil)
        {
            Debug.Log($"[GBX] ChooseSoil({soil})");
            _ui.SetSoilIndex(soil);
        }

        /// Click Place camera: PlaceCamera (CloseUI is Invoke'd 0.5 s later).
        IEnumerator PressPlace()
        {
            Debug.Log("[GBX] PressPlace");
            _ui.PlaceCamera();
            yield return new WaitForSeconds(0.75f);
        }

        IEnumerator PlaceAllThree(int soilForBox0, int soilForBox1, int soilForBox2)
        {
            yield return OpenBox(0); if (soilForBox0 >= 0) ChooseSoil(soilForBox0); yield return PressPlace();
            yield return OpenBox(1); if (soilForBox1 >= 0) ChooseSoil(soilForBox1); yield return PressPlace();
            yield return OpenBox(2); if (soilForBox2 >= 0) ChooseSoil(soilForBox2); yield return PressPlace();
            // The wrap-up dialogue after the third placement (conversation 92, nodes 32-33) sets this.
            _finished.Value = true;
            Debug.Log("[GBX] all three placed; finished = true");
        }

        // ---- setup / cleanup ----

        IEnumerator Setup()
        {
            // Leftovers of an aborted earlier test (see GardenBoxTestObject).
            foreach (var m in Object.FindObjectsByType<GardenBoxTestObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (m != null) Object.DestroyImmediate(m.gameObject);
            }
            _problems = new List<string>();
            _extra = new List<Object>();
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            Debug.Log("[GBX] setup: log hook");

            _planterIndex = Load<IntVariable>("IntVariable_GardenBoxes_PlanterIndex");
            _soils = new[]
            {
                Load<IntVariable>("IntVariable_GardenBoxes_Planter1SoilSelection"),
                Load<IntVariable>("IntVariable_GardenBoxes_Planter2SoilSelection"),
                Load<IntVariable>("IntVariable_GardenBoxes_Planter3SoilSelection"),
            };
            _finished = Load<BoolVariable>("BoolVariable_FinishedPlacingCameras");
            Debug.Log("[GBX] setup: assets loaded");
            foreach (var s in _soils) s.Value = -1;
            _finished.Value = false;
            _planterIndex.Value = -1;
            _introPlayed = new bool[3];
            Debug.Log("[GBX] setup: variables reset");

            // The dialogue system, as the Unit 4 scene has it: the database (with the Desert Delicacies
            // quest and the variables), the Lua environment, the dialogue UI. Without a DialogueSystemController
            // the first DialogueLua/QuestLog call overflows the stack in this project.
            // A DialogueSystemController destroys itself if another one exists, so remove the previous test's first.
            foreach (var old in Object.FindObjectsByType<DialogueSystemController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(old.gameObject);
            }
            var dsPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DialogueSystemPrefabPath);
            Assert.NotNull(dsPrefab, DialogueSystemPrefabPath);
            var dialogueSystem = Object.Instantiate(dsPrefab);
            dialogueSystem.AddComponent<GardenBoxTestObject>();
            _extra.Add(dialogueSystem);
            Debug.Log("[GBX] setup: dialogue system instantiated");
            yield return null; // its Start
            // Its "Dialogue Canvas" is renamed so that GameObject.Find("Dialogue Canvas") in the garden box
            // code finds the stand-in below, whose Canvas the tests control.
            foreach (var t in dialogueSystem.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Dialogue Canvas") t.name = "Dialogue Canvas (dialogue system)";
            }
            Debug.Log("[GBX] setup: dialogue system ready");

            // Stand-in for the game's "Dialogue Canvas". AnimateWateringPlant waits while its Canvas is enabled.
            var dc = NewObject("Dialogue Canvas");
            _dialogueCanvas = dc.AddComponent<Canvas>();
            _dialogueCanvas.enabled = false;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.NotNull(prefab, PrefabPath);
            _root = Object.Instantiate(prefab);
            _root.AddComponent<GardenBoxTestObject>();
            _ui = _root.GetComponent<GardenBoxesUIController>();
            Assert.NotNull(_ui, "GardenBoxesUIController on the prefab root");
            Debug.Log("[GBX] setup: prefab instantiated");

            // The Place button's second listener is GardenBoxLogger.LogCameraPlaced; keep GameLogger out of a test.
            Object.DestroyImmediate(_root.GetComponent<GardenBoxLogger>());

            // The prefab's dialogue triggers would start conversations; there is no Dialogue Manager here.
            foreach (var t in _root.GetComponentsInChildren<DialogueSystemTrigger>(true))
            {
                var ts = new SerializedObject(t);
                ts.FindProperty("conversation").stringValue = "";
                ts.ApplyModifiedPropertiesWithoutUndo();
                t.enabled = false;
            }
            Debug.Log("[GBX] setup: triggers neutralised");

            // What the Unit 4 scene's prefab instance assigns and the prefab leaves empty.
            var so = new SerializedObject(_ui);
            foreach (var n in new[] { "turnipCollider", "potatoesCollider", "orangeCollider", "broccoliCollider" })
            {
                so.FindProperty(n).objectReferenceValue = NewObject(n).AddComponent<BoxCollider>();
            }
            var usProp = so.FindProperty("plantUsables");
            _usables = new Usable[usProp.arraySize];
            for (int i = 0; i < usProp.arraySize; i++)
            {
                _usables[i] = NewObject("Planter usable " + i).AddComponent<Usable>();
                usProp.GetArrayElementAtIndex(i).objectReferenceValue = _usables[i];
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            _placeButton = (Button)so.FindProperty("buttons").GetArrayElementAtIndex(0).objectReferenceValue;
            Assert.NotNull(_placeButton, "buttons[0] is the Place button in the prefab");
            var fl = so.FindProperty("_inactiveCameraImagesToFlash");
            _flash = new Animator[fl.arraySize];
            for (int i = 0; i < fl.arraySize; i++) _flash[i] = (Animator)fl.GetArrayElementAtIndex(i).objectReferenceValue;
            Assert.AreEqual(3, _flash.Length, "three camera images to flash");
            Debug.Log("[GBX] setup: colliders, usables, refs");

            var helper = _root.GetComponent<GardenBoxesEventHelper>();
            var sw = NewObject("Scene object switcher stand-in");
            var hso = new SerializedObject(helper);
            hso.FindProperty("_objectSwitcher").objectReferenceValue = sw.AddComponent<Tools.StateManagement.ObjectSwitcher>();
            hso.ApplyModifiedPropertiesWithoutUndo();

            _data = ScriptableObject.CreateInstance<SOGardenBoxes_PlanterData>();
            _extra.Add(_data);
            Debug.Log("[GBX] setup: done");
            yield return null; // Start()
        }

        void Cleanup()
        {
            Application.logMessageReceived -= OnLog;
            try
            {
                var dir = Path.Combine(Application.dataPath, "../Logs");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "GardenBoxCrashRepro-" + TestContext.CurrentContext.Test.Name + ".txt"),
                    _problems == null || _problems.Count == 0 ? "no errors or exceptions logged\n" : string.Join("\n----\n", _problems) + "\n");
            }
            catch (System.Exception e) { Debug.LogWarning("could not write the problem file: " + e.Message); }
            // Immediate, so pending Invokes and coroutines of this test cannot run into the next one.
            if (_root != null) Object.DestroyImmediate(_root);
            if (_extra != null) foreach (var o in _extra) if (o != null) Object.DestroyImmediate(o);
            if (_soils != null) foreach (var s in _soils) if (s != null) s.Value = -1;
            if (_finished != null) _finished.Value = false;
            if (_planterIndex != null) _planterIndex.Value = 0;
        }

        GameObject NewObject(string name)
        {
            var g = new GameObject(name);
            g.AddComponent<GardenBoxTestObject>();
            _extra.Add(g);
            return g;
        }

        string Problems() => "errors or exceptions were logged:\n" + string.Join("\n----\n", _problems);

        static T Load<T>(string name) where T : Object
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(DataFolder + name + ".asset");
            Assert.NotNull(a, DataFolder + name + ".asset");
            return a;
        }

        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
                _problems.Add(type + ": " + condition + "\n" + stackTrace);
        }
    }
}
#endif
