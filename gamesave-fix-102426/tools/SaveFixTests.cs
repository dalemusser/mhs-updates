#if UNITY_EDITOR
// PlayMode tests for the game's saving (mhs-updates/gamesave-fix-102426): the real
// GameSaveManager and DatabaseSaver against a fake save service on localhost that can be
// up, down (503), refusing (401), rejecting (400), answering HTML, or hanging.
//
// Setup and cleanup are inline (not [UnitySetUp]/[UnityTearDown]): in this project those made the
// editor's test runner overflow its stack (see unit4-gardenbox-crash-102326/tools/README.md).
//
// Run headless:
//   Unity -batchmode -projectPath <project> -runTests -testPlatform PlayMode \
//         -testFilter MHS.SaveFixTests -testResults results.xml -logFile unity.log
// (needs ProjectSettings.asset playModeTestRunnerEnabled: 1 and Assets/csc.rsp referencing
//  Library/ScriptAssemblies/UnityEngine.TestRunner.dll so Assembly-CSharp can hold these tests).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Obvious.Soap;
using PixelCrushers;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MHS.SaveFixTests
{
    /// A saver with one string, so a save has recognisable content and applying it is observable.
    public class SaveFixTestSaver : Saver
    {
        [Serializable] class D { public string v; }
        public string value = "v0";
        public int applied;
        public string lastApplied;
        public override string RecordData() => JsonUtility.ToJson(new D { v = value });
        public override void ApplyData(string s)
        {
            applied++;
            lastApplied = string.IsNullOrEmpty(s) ? null : JsonUtility.FromJson<D>(s).v;
        }
    }

    /// The fake save service: POST /api/state/save and /api/state/load on 127.0.0.1.
    public class FakeSaveService : IDisposable
    {
        public string BaseUrl;
        public volatile string mode = "ok"; // ok | down | refuse | reject | html | hang | toolarge | empty
        public volatile string loadTimestamp = null; // when set, the "timestamp" of every save in a load answer
        public volatile int hangMs = 6000;
        public readonly List<string> saveBodies = new List<string>();
        public readonly List<string> loadBodies = new List<string>();
        public readonly List<string> stored = new List<string>(); // save_data json, oldest first
        public int SaveCount { get { lock (saveBodies) return saveBodies.Count; } }
        public int LoadCount { get { lock (loadBodies) return loadBodies.Count; } }
        HttpListener _listener;
        Thread _thread;
        volatile bool _stop;

        public FakeSaveService()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        void Loop()
        {
            while (!_stop)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            string path = ctx.Request.Url.AbsolutePath;
            bool isSave = path.EndsWith("/save");
            lock (isSave ? saveBodies : loadBodies) (isSave ? saveBodies : loadBodies).Add(body);
            string m = mode;
            if (m == "hang") Thread.Sleep(hangMs);
            int status = 200;
            string text;
            string type = "application/json";
            switch (m == "hang" ? "ok" : m)
            {
                case "down": status = 503; text = "{\"error\":\"down\"}"; break;
                case "refuse": status = 401; text = "Invalid API key\n"; type = "text/plain"; break;
                case "reject": status = 400; text = "{\"error\":\"Missing required fields\"}"; break;
                case "toolarge": status = 413; text = "{\"error\":\"too large\"}"; break;
                case "html": status = 200; text = "<html><body>Blocked by the filter</body></html>"; type = "text/html"; break;
                case "empty": status = 200; text = ""; break;
                default:
                    if (isSave)
                    {
                        int i = body.IndexOf("\"save_data\":", StringComparison.Ordinal);
                        string sd = i >= 0 ? body.Substring(i + "\"save_data\":".Length).TrimEnd() : "{}";
                        if (sd.EndsWith("}")) sd = sd.Substring(0, sd.Length - 1); // the payload's closing brace
                        lock (stored) stored.Add(sd);
                        status = 201;
                        text = "{\"id\":\"000000000000000000000abc\",\"user_id\":\"x\",\"game\":\"mhs\",\"timestamp\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
                    }
                    else
                    {
                        var sb = new StringBuilder("[");
                        lock (stored)
                        {
                            for (int k = stored.Count - 1; k >= 0; k--)
                            {
                                if (sb.Length > 1) sb.Append(',');
                                sb.Append("{\"id\":\"000000000000000000000abc\",\"user_id\":\"x\",\"game\":\"mhs\",\"timestamp\":\"" + (loadTimestamp ?? DateTime.UtcNow.ToString("o")) + "\",\"save_data\":").Append(stored[k]).Append('}');
                            }
                        }
                        sb.Append(']');
                        text = sb.ToString();
                    }
                    break;
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = type;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception) { }
        }

        public string NewestStored { get { lock (stored) return stored.Count > 0 ? stored[stored.Count - 1] : null; } }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
        }
    }

    /// One "launch": a GameSaveManager + DatabaseSaver with runtime Soap variables, and a test saver.
    public class Rig
    {
        public GameObject go;
        public GameObject saverGo;
        public DatabaseSaver saver;
        public GameSaveManager mgr;
        public BoolVariable autoSave, inProgress, debugLogs;
        public StringVariable playerId;
        public IntVariable slot;
        public ScriptableEventInt saveEvent, loadEvent;
        public SaveFixTestSaver testSaver;

        public static Rig Build(string uid)
        {
            var r = new Rig();
            r.autoSave = ScriptableObject.CreateInstance<BoolVariable>();
            r.inProgress = ScriptableObject.CreateInstance<BoolVariable>();
            r.debugLogs = ScriptableObject.CreateInstance<BoolVariable>();
            r.playerId = ScriptableObject.CreateInstance<StringVariable>();
            r.slot = ScriptableObject.CreateInstance<IntVariable>();
            r.saveEvent = ScriptableObject.CreateInstance<ScriptableEventInt>();
            r.loadEvent = ScriptableObject.CreateInstance<ScriptableEventInt>();
            r.playerId.Value = uid;

            r.saverGo = new GameObject("SaveFixTestSaver");
            r.testSaver = r.saverGo.AddComponent<SaveFixTestSaver>();
            r.testSaver.key = "SaveFixTestSaver";

            r.go = new GameObject("SaveFixRig");
            r.go.SetActive(false); // fields must be set before OnEnable asserts them
            r.mgr = r.go.AddComponent<GameSaveManager>();
            Set(r.mgr, "enableDebugLogs", r.debugLogs);
            r.saver = r.go.AddComponent<DatabaseSaver>();
            Set(r.saver, "slotNumber", r.slot);
            Set(r.saver, "OnSaveGame", r.saveEvent);
            Set(r.saver, "OnLoadGameFromSlot", r.loadEvent);
            Set(r.saver, "autoSave", r.autoSave);
            Set(r.saver, "playerId", r.playerId);
            Set(r.saver, "enableDebugLogs", r.debugLogs);
            Set(r.saver, "saveGameInProgress", r.inProgress);
            r.go.SetActive(true);
            return r;
        }

        static void Set(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(f, "field " + field);
            f.SetValue(target, value);
        }

        public void Destroy()
        {
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
            if (saverGo != null) UnityEngine.Object.DestroyImmediate(saverGo);
        }
    }

    public class SaveFixTests
    {
        const string Uid = "0123456789abcdef01234567";
        const string Auth = "Bearer test-secret-auth-value";
        FakeSaveService _svc;
        Rig _rig;
        List<string> _logs;
        Application.LogCallback _logHandler;

        // ---- setup / cleanup (inline) ----

        IEnumerator Setup()
        {
            var ss = SaveSystem.instance; // the PixelCrushers singleton (created on first use)
            Assert.IsNotNull(ss);
            typeof(DatabaseSaver).GetProperty("saveData").SetValue(null, null);
            PlayerPrefs.DeleteKey("mhs_last_save");
            PlayerPrefs.DeleteKey("mhs_pending_save_" + Uid);
            PlayerPrefs.Save();
            _svc = new FakeSaveService();
            GameSaveManager.saveConfigOverride = () => new MHSBridge.ServiceConfig { url = _svc.BaseUrl + "api/state/save", auth = Auth };
            GameSaveManager.loadConfigOverride = () => new MHSBridge.ServiceConfig { url = _svc.BaseUrl + "api/state/load", auth = Auth };
            GameSaveManager.requestTimeoutSeconds = 30;
            _logs = new List<string>();
            _logHandler = (msg, stack, type) => { lock (_logs) _logs.Add(msg); };
            Application.logMessageReceivedThreaded += _logHandler;
            yield return Launch();
        }

        // A fresh "launch" of the game on the same device (same PlayerPrefs).
        IEnumerator Launch()
        {
            _rig?.Destroy();
            _rig = Rig.Build(Uid);
            yield return null; // Start runs (and switches autoSave off in the Editor)
            yield return null;
            _rig.autoSave.Value = true;
        }

        void Cleanup()
        {
            Application.logMessageReceivedThreaded -= _logHandler;
            GameSaveManager.saveConfigOverride = null;
            GameSaveManager.loadConfigOverride = null;
            GameSaveManager.requestTimeoutSeconds = 30;
            _rig?.Destroy();
            _rig = null;
            _svc?.Dispose();
            _svc = null;
            PlayerPrefs.DeleteKey("mhs_last_save");
            PlayerPrefs.DeleteKey("mhs_pending_save_" + Uid);
            PlayerPrefs.Save();
        }

        IEnumerator WaitUntil(Func<bool> cond, float seconds, string what)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!cond())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for: " + what + " (status: '" + (_rig?.saver?.StatusText ?? "") + "')");
                yield return null;
            }
        }

        IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        void AssertNoAuthInLogs()
        {
            lock (_logs)
            {
                foreach (var m in _logs) StringAssert.DoesNotContain("test-secret-auth-value", m);
            }
        }

        string ServerSave(string value, DateTime savedAt)
        {
            var d = new SavedGameData();
            d.sceneName = SceneManager.GetActiveScene().name;
            d.SetData("SaveFixTestSaver", -1, "{\"v\":\"" + value + "\"}");
            string json = JsonUtility.ToJson(d);
            var meta = new GameSaveManager.SaveMeta { seq = 1, saved_at = savedAt.ToString("o"), session_id = "server", version = "test", scene = d.sceneName, checkpoint = 3 };
            return GameSaveManager.WithMeta(json, meta);
        }

        // ---- 1. pure helpers ----

        [Test]
        public void T01_PayloadHelpers()
        {
            var d = new SavedGameData { sceneName = "Unit 1 Dev" };
            d.SetData("K", -1, "{\"a\":1}");
            string json = JsonUtility.ToJson(d);
            var meta = new GameSaveManager.SaveMeta { seq = 7, saved_at = "2026-10-24T20:00:00.0000000Z", session_id = "s", version = "v", scene = "Unit 1 Dev", checkpoint = 2 };
            string withMeta = GameSaveManager.WithMeta(json, meta);
            StringAssert.StartsWith("{\"mhs_meta\":{", withMeta);
            var back = JsonUtility.FromJson<SavedGameData>(withMeta);
            Assert.AreEqual("Unit 1 Dev", back.sceneName);
            Assert.AreEqual("{\"a\":1}", back.GetData("K"));
            var m = GameSaveManager.ReadMeta(withMeta);
            Assert.AreEqual(7, m.seq);
            Assert.AreEqual(2, m.checkpoint);
            Assert.IsNull(GameSaveManager.ReadMeta(json));

            string payload = GameSaveManager.BuildSavePayload(Uid, "mhs", withMeta);
            StringAssert.StartsWith("{\"user_id\":\"" + Uid + "\",\"game\":\"mhs\",\"save_data\":{", payload);
            Assert.IsTrue(payload.EndsWith("}}"));

            Assert.IsTrue(GameSaveManager.TryParseLoadBody("[]", out var s0, out _, out _)); Assert.AreEqual(0, s0.Length);
            Assert.IsTrue(GameSaveManager.TryParseLoadBody(" [ ] ", out var s1, out _, out _)); Assert.AreEqual(0, s1.Length);
            Assert.IsFalse(GameSaveManager.TryParseLoadBody("", out _, out _, out var emptyErr)); StringAssert.Contains("empty", emptyErr);
            Assert.IsFalse(GameSaveManager.TryParseLoadBody("null", out _, out _, out _));
            string body = "[{\"id\":\"1\",\"user_id\":\"" + Uid + "\",\"game\":\"mhs\",\"timestamp\":\"t\",\"save_data\":" + withMeta + "}]";
            Assert.IsTrue(GameSaveManager.TryParseLoadBody(body, out var s2, out var metas, out _));
            Assert.AreEqual(1, s2.Length);
            Assert.AreEqual("Unit 1 Dev", s2[0].save_data.sceneName);
            Assert.AreEqual(7, metas[0].seq);
            Assert.IsFalse(GameSaveManager.TryParseLoadBody("<html>blocked</html>", out _, out _, out var err));
            StringAssert.Contains("not a list", err);
            Assert.IsTrue(GameSaveManager.IsValidUserId(Uid));
            Assert.IsFalse(GameSaveManager.IsValidUserId("mhs_developer"));
            Assert.IsFalse(GameSaveManager.IsValidUserId(""));
        }

        // ---- 2. saving ----

        [UnityTest]
        public IEnumerator T02_SaveRetriesUntilAccepted_WithCountdown_AndNoAuthInLogs()
        {
            yield return Setup();
            try
            {
                _svc.mode = "down";
                _rig.testSaver.value = "first";
                _rig.saveEvent.Raise(0);
                Assert.IsTrue(_rig.saver.HasUnsentSave);
                Assert.IsTrue(_rig.inProgress.Value);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first failure and countdown");
                StringAssert.Contains("attempt 2", _rig.saver.StatusText);
                Assert.AreEqual(1, _svc.SaveCount);
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "the retry to be accepted");
                Assert.AreEqual(2, _svc.SaveCount, "one retry after the service came back");
                Assert.IsFalse(_rig.inProgress.Value);
                Assert.AreEqual("Game Saved!", _rig.saver.StatusText);
                StringAssert.Contains("\"mhs_meta\":{", _svc.NewestStored);
                StringAssert.Contains("\\\"v\\\":\\\"first\\\"", _svc.NewestStored);
                StringAssert.Contains("\"user_id\":\"" + Uid + "\"", _svc.saveBodies[1]);
                yield return Wait(2.6f);
                Assert.AreEqual("", _rig.saver.StatusText, "the toast goes away");
                AssertNoAuthInLogs();
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T03_NewerSaveReplacesTheUnsentOne()
        {
            yield return Setup();
            try
            {
                _svc.mode = "down";
                _rig.testSaver.value = "one";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                _rig.testSaver.value = "two";
                _rig.saveEvent.Raise(0);
                yield return null;
                int before = _svc.SaveCount;
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "acceptance");
                Assert.AreEqual(before + 1, _svc.SaveCount, "exactly one request once the service is back");
                Assert.AreEqual(1, _svc.stored.Count);
                StringAssert.Contains("\\\"v\\\":\\\"two\\\"", _svc.NewestStored);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T04_UnsentSaveSurvivesRelaunch_AndIsSentThen()
        {
            yield return Setup();
            try
            {
                _svc.mode = "down";
                _rig.testSaver.value = "kept";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.AreEqual(Uid, rec.user_id);
                Assert.IsFalse(rec.sent);
                StringAssert.Contains("\\\"v\\\":\\\"kept\\\"", rec.save_json);

                // Relaunch with the service reachable: the pending save is checked against the
                // service's newest (none) and sent.
                _svc.mode = "ok";
                yield return Launch();
                yield return WaitUntil(() => _svc.stored.Count == 1, 15, "the recovered save to be sent");
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 5, "the record to be marked sent");
                StringAssert.Contains("\\\"v\\\":\\\"kept\\\"", _svc.NewestStored);
                var rec2 = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec2.sent);
                Assert.IsFalse(PlayerPrefs.HasKey("mhs_pending_save_" + Uid));
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T05_RecoveredSaveIsDroppedWhenTheServiceHoldsNewerProgress()
        {
            yield return Setup();
            try
            {
                _svc.mode = "down";
                _rig.testSaver.value = "stale";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                // Meanwhile (another device) the service got newer progress.
                _svc.stored.Add(ServerSave("newer-elsewhere", DateTime.UtcNow.AddMinutes(5)));
                _svc.mode = "ok";
                int before = _svc.SaveCount;
                yield return Launch();
                yield return WaitUntil(() => _rig.saver.ServerAnswered, 10, "server answer");
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 5, "the stale save to be dropped");
                Assert.AreEqual(before, _svc.SaveCount, "the stale save was not sent");
                Assert.AreEqual(1, _svc.stored.Count);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T06_RefusedKeyIsRetried_NotDropped()
        {
            yield return Setup();
            try
            {
                _svc.mode = "refuse";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("refused this game's access key"), 10, "the refused-key status");
                Assert.IsTrue(_rig.saver.HasUnsentSave);
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "acceptance after the key is accepted again");
                Assert.AreEqual(1, _svc.stored.Count);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T07_RejectedPayloadIsNotRetried()
        {
            yield return Setup();
            try
            {
                _svc.mode = "reject";
                LogAssert.ignoreFailingMessages = true;
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the rejection to end the save");
                yield return Wait(1f);
                Assert.AreEqual(1, _svc.SaveCount, "no retry after a 400");
                Assert.IsFalse(_rig.inProgress.Value);
                StringAssert.Contains("Save Failed", _rig.saver.StatusText);
            }
            finally { LogAssert.ignoreFailingMessages = false; Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T08_TimeoutIsAFailureThatIsRetried()
        {
            yield return Setup();
            try
            {
                GameSaveManager.requestTimeoutSeconds = 2;
                _svc.mode = "hang";
                _svc.hangMs = 8000;
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("did not answer in time"), 8, "the timeout status");
                Assert.IsTrue(_rig.saver.HasUnsentSave);
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "acceptance after the service answers");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T09_SaveWaitsForAValidPlayerId()
        {
            yield return Setup();
            try
            {
                _rig.playerId.Value = "";
                _rig.saveEvent.Raise(0);
                yield return Wait(2f);
                Assert.AreEqual(0, _svc.SaveCount, "nothing is sent without an id");
                StringAssert.Contains("waiting for your sign-in", _rig.saver.StatusText);
                _rig.playerId.Value = "mhs_developer";
                yield return Wait(1.5f);
                Assert.AreEqual(0, _svc.SaveCount, "a non-hex id is not sent either");
                _rig.playerId.Value = Uid;
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the save once the id is valid");
                StringAssert.Contains("\"user_id\":\"" + Uid + "\"", _svc.saveBodies[0]);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T10_TransitionGateWaitsForTheSave_ButNotBeyondTheCap()
        {
            yield return Setup();
            try
            {
                _svc.mode = "down";
                _rig.saveEvent.Raise(0);
                float start = Time.realtimeSinceStartup;
                _rig.saver.CallToTransitionToNextUnit();
                Assert.IsFalse(_rig.saver.ReadyToTransition, "not ready while the save is unsent");
                float readyAt = -1;
                while (Time.realtimeSinceStartup - start < 8f)
                {
                    _rig.saver.CallToTransitionToNextUnit();
                    if (_rig.saver.ReadyToTransition) { readyAt = Time.realtimeSinceStartup - start; break; }
                    yield return null;
                    yield return null; // the real loader polls a not-ready check every second frame
                }
                Assert.IsTrue(readyAt >= 4.5f && readyAt <= 6.5f, "ready after about five seconds, was " + readyAt);
                Assert.IsTrue(_rig.saver.HasUnsentSave, "the save is still kept");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsFalse(rec.sent);
            }
            finally { Cleanup(); }
        }

        // ---- 3. loading ----

        [UnityTest]
        public IEnumerator T11_FailedLoadNeverStartsOver_ThenResumesWhenTheServiceAnswers()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("server-1", DateTime.UtcNow.AddMinutes(-10)));
                _svc.mode = "down";
                _rig.loadEvent.Raise(0); // START GAME
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 1 && _rig.saver.StatusText.Contains("Can't load your progress yet"), 10, "the first load failure");
                StringAssert.Contains("Retrying in", _rig.saver.StatusText);
                Assert.IsNull(DatabaseSaver.saveData, "no save applied, no new game started");
                Assert.AreEqual(0, _rig.testSaver.applied);
                Assert.IsTrue(_rig.saver.StartRunning);
                _svc.mode = "ok";
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "the resume once the service answers");
                Assert.AreEqual("server-1", _rig.testSaver.lastApplied);
                Assert.IsNotNull(DatabaseSaver.saveData);
                yield return WaitUntil(() => !_rig.saver.StartRunning, 5, "start to finish");
                Assert.AreEqual("", _rig.saver.StatusText);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T12_NoSavesStartsANewGame_AndHtmlIsNotNoSaves()
        {
            yield return Setup();
            try
            {
                _svc.mode = "html";
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 1, 10, "the unreadable body to count as a failure");
                Assert.IsFalse(_rig.saver.ServerAnswered);
                Assert.IsNull(DatabaseSaver.saveData);
                _svc.mode = "ok"; // nothing stored: "no saves"
                yield return WaitUntil(() => _rig.saver.ServerAnswered && !_rig.saver.StartRunning, 15, "the no-saves answer");
                Assert.IsNull(DatabaseSaver.saveData, "a new game: nothing applied");
                Assert.AreEqual(0, _rig.testSaver.applied);
                Assert.AreEqual("", _rig.saver.StatusText);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T13_DeviceCopyIsUsedWhileTheServiceIsDown_AndTheServiceConfirms()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "local-1";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the save to be accepted");
                Assert.AreEqual(1, _svc.stored.Count);

                _svc.mode = "down";
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                Assert.AreEqual("local-1", _rig.testSaver.lastApplied);
                StringAssert.Contains("Using the progress saved on this device", _rig.saver.StatusText);
                yield return Wait(1.5f);
                StringAssert.Contains("Using the progress saved on this device", _rig.saver.StatusText, "the countdown does not replace it");

                _svc.mode = "ok"; // the service holds the same save
                yield return WaitUntil(() => _rig.saver.ServerAnswered && !_rig.saver.StartRunning, 15, "the service to confirm");
                yield return Wait(0.5f);
                Assert.AreEqual(1, _rig.testSaver.applied, "the same save is not applied twice");
                Assert.AreEqual("", _rig.saver.StatusText);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T14_NewerProgressOnTheServiceWinsOverTheDeviceCopy()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "local-1";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the save to be accepted");

                _svc.mode = "down";
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                Assert.AreEqual("local-1", _rig.testSaver.lastApplied);

                _svc.stored.Add(ServerSave("server-2", DateTime.UtcNow.AddMinutes(5))); // made on another device meanwhile
                _svc.mode = "ok";
                yield return WaitUntil(() => _rig.testSaver.applied == 2, 15, "the newer server save to be applied");
                Assert.AreEqual("server-2", _rig.testSaver.lastApplied);
                yield return WaitUntil(() => !_rig.saver.StartRunning, 5, "start to finish");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T16_IdenticalRepeatOfACheckpointIsNotSentAgain()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "same";
                _rig.saveEvent.Raise(0);
                _rig.saveEvent.Raise(0); // the second raiser of the same checkpoint, same frame
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "acceptance");
                yield return Wait(0.5f);
                Assert.AreEqual(1, _svc.SaveCount, "one request for two identical raises");
                _rig.saveEvent.Raise(0); // later again, still identical
                yield return Wait(0.5f);
                Assert.AreEqual(1, _svc.SaveCount, "an identical later save is not sent either");
                _rig.testSaver.value = "changed";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.SaveCount == 2, 10, "a changed save is sent");
                StringAssert.Contains("\\\"v\\\":\\\"changed\\\"", _svc.NewestStored);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T17_SaveAnsweredWithAnHtmlPageIsNotAccepted()
        {
            yield return Setup();
            try
            {
                _svc.mode = "html"; // a filter's page with a 200, as a captive portal or a redirect produces
                _rig.testSaver.value = "behind-a-filter";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("answered with something unreadable"), 10, "the unreadable-answer status");
                Assert.IsTrue(_rig.saver.HasUnsentSave, "not marked sent");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsFalse(rec.sent);
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "acceptance once the service answers properly");
                Assert.AreEqual(1, _svc.stored.Count);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T18_EmptyLoadAnswerIsAFailure_NotNoSaves()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("server-1", DateTime.UtcNow.AddMinutes(-10)));
                _svc.mode = "empty";
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 1, 10, "the empty body to count as a failure");
                Assert.IsFalse(_rig.saver.ServerAnswered);
                Assert.IsNull(DatabaseSaver.saveData, "no new game started");
                _svc.mode = "ok";
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "the resume once the service answers");
                Assert.AreEqual("server-1", _rig.testSaver.lastApplied);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T19_ACopyTheServiceRejectedIsStillResumedWhenTheServiceHasNothing()
        {
            yield return Setup();
            try
            {
                _svc.mode = "reject";
                LogAssert.ignoreFailingMessages = true;
                _rig.testSaver.value = "only-here";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the rejection to end the save");
                _svc.mode = "ok"; // nothing stored
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the rejected copy");
                Assert.AreEqual("only-here", _rig.testSaver.lastApplied);
                Assert.AreEqual(0, _svc.stored.Count, "the rejected copy is not resent");
            }
            finally { LogAssert.ignoreFailingMessages = false; Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T20_OlderBuildSaveWithoutMeta_ServerClockDecidesWithAnAllowance()
        {
            yield return Setup();
            try
            {
                // An unsent save on this device...
                _svc.mode = "down";
                _rig.testSaver.value = "device";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                // ...and on the service a save made by an older build (no mhs_meta) whose server
                // timestamp is only 30 s later: within the clock allowance, the device's save wins.
                var old = new SavedGameData { sceneName = SceneManager.GetActiveScene().name };
                old.SetData("SaveFixTestSaver", -1, "{\"v\":\"old-build\"}");
                _svc.stored.Add(JsonUtility.ToJson(old));
                _svc.loadTimestamp = DateTime.UtcNow.AddSeconds(30).ToString("o");
                _svc.mode = "ok";
                yield return Launch();
                yield return WaitUntil(() => _rig.saver.ServerAnswered && !_rig.saver.HasUnsentSave, 15, "the recovered save to be decided");
                Assert.AreEqual(2, _svc.stored.Count, "the device's save was sent");
                StringAssert.Contains("\\\"v\\\":\\\"device\\\"", _svc.NewestStored);

                // A server-clock timestamp an hour later is beyond any clock skew: the service wins.
                _svc.stored.Clear();
                _svc.stored.Add(JsonUtility.ToJson(old));
                _svc.loadTimestamp = DateTime.UtcNow.AddHours(1).ToString("o");
                _svc.mode = "down";
                yield return Launch();
                _rig.testSaver.value = "device-2";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                _svc.mode = "ok";
                int before = _svc.SaveCount;
                yield return Launch();
                yield return WaitUntil(() => _rig.saver.ServerAnswered && !_rig.saver.HasUnsentSave, 15, "the recovered save to be decided");
                Assert.AreEqual(before, _svc.SaveCount, "the device's older save was dropped, not sent");
                Assert.AreEqual(1, _svc.stored.Count);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T15_OneLoadRequestPerLaunch_MenuAndStartShareIt()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("server-1", DateTime.UtcNow.AddMinutes(-10)));
                // The main menu asks as soon as the player is known.
                typeof(DatabaseSaver).GetMethod("ApplyGameProgressFromDatabase", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_rig.saver, null);
                yield return WaitUntil(() => _rig.saver.ServerAnswered, 10, "the menu's fetch");
                _rig.loadEvent.Raise(0); // START GAME
                yield return WaitUntil(() => _rig.testSaver.applied >= 1 && !_rig.saver.StartRunning, 10, "start");
                Assert.AreEqual(1, _svc.LoadCount, "START reuses the menu's answer");
                Assert.AreEqual("server-1", _rig.testSaver.lastApplied);
            }
            finally { Cleanup(); }
        }
    }
}
#endif
