#if UNITY_EDITOR
// PlayMode tests for the game's saving (mhs-updates/gamesave-fix-102426): the real
// GameSaveManager and DatabaseSaver against a fake save service on localhost that can be
// up, down (503), refusing (401), rejecting (400), answering HTML, or hanging; and the
// GameLogger's unit-transition gate (T21), with no log endpoint so nothing is sent; and the
// findings of the second review of 2026-10-25 (T22-T30) and of the reviews of versions 1.2 (T31-T38) and 1.3 (T39-T42), and the focused reviews of the base logic (T43-T56).
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
            if (lastApplied != null) value = lastApplied; // restored state is the state recorded next, as in a real saver
        }
    }

    /// The fake save service: POST /api/state/save and /api/state/load on 127.0.0.1.
    public class FakeSaveService : IDisposable
    {
        public string BaseUrl;
        public volatile string mode = "ok"; // ok | down | refuse | reject | html | hang | toolarge | empty | savesdown (saves 503, loads ok) | saveshang (saves sleep hangMs, loads at once)
        public volatile string loadTimestamp = null; // when set, the "timestamp" of every save in a load answer
        public volatile int hangMs = 6000;
        public volatile bool loadsDown;      // loads answer 503 whatever the mode (saves as the mode says)
        public volatile int saveSleepMs;     // saves sleep this long before answering as the mode says
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
            if (m == "saveshang" && isSave) Thread.Sleep(hangMs);
            if (isSave && saveSleepMs > 0) Thread.Sleep(saveSleepMs);
            string eff = (m == "hang" || m == "saveshang") ? "ok" : m;
            if (eff == "savesdown") eff = isSave ? "down" : "ok";
            if (!isSave && loadsDown) eff = "down";
            switch (eff)
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
                        int limit = 1;
                        var lm = System.Text.RegularExpressions.Regex.Match(body, "\"limit\":(\\d+)");
                        if (lm.Success) limit = int.Parse(lm.Groups[1].Value);
                        var sb = new StringBuilder("[");
                        lock (stored)
                        {
                            for (int k = stored.Count - 1; k >= 0 && stored.Count - 1 - k < limit; k--)
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
            r.debugLogs.Value = true; // Print lines go through the console logger to Debug.Log, so the no-auth check sees them

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

        public static void Set(object target, string field, object value)
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
            PlayerPrefs.DeleteKey("mhs_pending_index");
            PlayerPrefs.Save();
            DatabaseSaver.serverAnswerMaxAgeSeconds = 60f;
            DatabaseSaver.noServiceConfigStartSeconds = 5f;
            DatabaseSaver.heldSaveMaxSeconds = 300f;
            EnsureConsole();
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
            typeof(DatabaseSaver).GetField("launchId", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, Guid.NewGuid().ToString("N")); // a page load's new session id
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
            if (_console != null) UnityEngine.Object.DestroyImmediate(_console);
            _console = null;
            DatabaseSaver.serverAnswerMaxAgeSeconds = 60f;
            DatabaseSaver.noServiceConfigStartSeconds = 5f;
            DatabaseSaver.heldSaveMaxSeconds = 300f;
            PlayerPrefs.DeleteKey("mhs_last_save");
            PlayerPrefs.DeleteKey("mhs_pending_save_" + Uid);
            PlayerPrefs.DeleteKey("mhs_pending_index");
            PlayerPrefs.Save();
        }

        GameObject _console;
        BoolVariable _consoleFlag;

        // The game's console logger prints the savers' Print lines through Debug.Log
        // when its flag is on, so the no-auth check also covers those lines.
        void EnsureConsole()
        {
            if (ConsoleLogManager.Instance != null) return;
            _consoleFlag = ScriptableObject.CreateInstance<BoolVariable>();
            _consoleFlag.Value = true;
            _console = new GameObject("SaveFixConsole");
            _console.SetActive(false);
            var clm = _console.AddComponent<ConsoleLogManager>();
            Rig.Set(clm, "enableDebugLogs", _consoleFlag);
            _console.SetActive(true);
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

        string ServerSave(string value, DateTime savedAt, string scene = null)
        {
            var d = new SavedGameData();
            d.sceneName = scene ?? SceneManager.GetActiveScene().name;
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
                _svc.mode = "ok";
                int before = _svc.SaveCount;
                _rig.testSaver.value = "two";
                _rig.saveEvent.Raise(0);
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
                Assert.AreEqual(0, _rig.saver.NewGamesStarted);
                _svc.mode = "ok";
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "the resume once the service answers");
                Assert.AreEqual("server-1", _rig.testSaver.lastApplied);
                Assert.IsNotNull(DatabaseSaver.saveData);
                Assert.AreEqual(0, _rig.saver.NewGamesStarted);
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
                Assert.AreEqual(1, _rig.saver.NewGamesStarted, "the new game was decided (the rig has no scene loader to run it)");
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
                Assert.AreEqual(0, _rig.saver.NewGamesStarted);
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
        // ---- 5. the logger's transition gate (the fourth file, GameLogger.cs) ----

        [UnityTest]
        public IEnumerator T21_LoggerTransitionGateOpensAfterTheCap_WithTheLogHostUnreachable()
        {
            // The v1.3 rule took a gap of more than one FRAME as a new transition request, and the
            // real loader polls a not-ready check every second frame, so the five-second cap never
            // fired: with the log host unreachable and an entry queued the unit transition waited
            // for ever. The test scene has no MHSBridge, so the logger has no endpoint, holds its
            // queue, and sends nothing anywhere; the cache key is restored afterwards.
            Assert.IsNull(MHSBridge.Instance, "no bridge in the test scene, so the logger cannot send");
            const string cacheKey = "game_logs_cache.json";
            string savedCache = PlayerPrefs.HasKey(cacheKey) ? PlayerPrefs.GetString(cacheKey) : null;
            PlayerPrefs.DeleteKey(cacheKey);
            PlayerPrefs.Save();
            GameLogger logger = GameLogger.Instance; // creates one (no logger exists in the test scene)
            try
            {
                yield return null; // Awake/Start
                logger.SetUserId(Uid);
                logger.LogEvent("savefix_test", new Dictionary<string, object> { { "k", "v" } });
                yield return null;
                Assert.IsTrue(GameLogger.IsSendingLogs, "the send loop is up and holding the entry");
                float start = Time.realtimeSinceStartup;
                logger.CallToTransitionToNextUnit();
                Assert.IsFalse(logger.ReadyToTransition, "not ready while the entry is unsent");
                float readyAt = -1;
                while (Time.realtimeSinceStartup - start < 8f)
                {
                    logger.CallToTransitionToNextUnit();
                    if (logger.ReadyToTransition) { readyAt = Time.realtimeSinceStartup - start; break; }
                    yield return null;
                    yield return null; // the real loader polls a not-ready check every second frame
                }
                Assert.IsTrue(readyAt >= 4.5f && readyAt <= 6.5f, "ready after about five seconds, was " + readyAt);
                Assert.IsTrue(GameLogger.IsSendingLogs, "the entry is still queued for the next unit's build");
            }
            finally
            {
                // Leave nothing of the test behind: a later Editor play session would send a cached entry.
                var queueLock = typeof(GameLogger).GetField("queueLock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                var queue = (Queue<Dictionary<string, object>>)typeof(GameLogger).GetField("logQueue", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(logger);
                lock (queueLock) queue.Clear();
                UnityEngine.Object.DestroyImmediate(logger.gameObject);
                PlayerPrefs.DeleteKey(cacheKey);
                if (savedCache != null) PlayerPrefs.SetString(cacheKey, savedCache);
                PlayerPrefs.Save();
                typeof(GameLogger).GetField("userId", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
            }
        }
        // ---- 6. the second review (2026-10-25) ----

        [UnityTest]
        public IEnumerator T22_StartAsksAgainWhenTheMenusAnswerIsOld()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("server-1", DateTime.UtcNow.AddMinutes(-10)));
                typeof(DatabaseSaver).GetMethod("ApplyGameProgressFromDatabase", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_rig.saver, null);
                yield return WaitUntil(() => _rig.saver.ServerAnswered, 10, "the menu's fetch");
                Assert.AreEqual(1, _svc.LoadCount);
                // The tab sits at the menu; meanwhile another device saves newer progress.
                DatabaseSaver.serverAnswerMaxAgeSeconds = 1f;
                _svc.stored.Add(ServerSave("server-2", DateTime.UtcNow));
                yield return Wait(1.5f);
                _rig.loadEvent.Raise(0); // START GAME
                yield return WaitUntil(() => _rig.testSaver.applied >= 1 && !_rig.saver.StartRunning, 10, "start");
                Assert.AreEqual(2, _svc.LoadCount, "START asked again");
                Assert.AreEqual("server-2", _rig.testSaver.lastApplied);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T23_ProgressMadeSinceADeviceCopyResumeIsNotRewoundByTheServicesAnswer()
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
                // Played on: a checkpoint recorded (unsent) after the resume, beyond the window in
                // which a checkpoint counts as the apply's own re-fire.
                DateTime between = DateTime.UtcNow;
                yield return Wait(5.5f);
                _rig.testSaver.value = "local-2";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.HasUnsentSave, 5, "the new save to be recorded");
                // The service comes back holding a save made elsewhere between the copy and the new progress.
                _svc.stored.Clear();
                _svc.stored.Add(ServerSave("server-between", between));
                _svc.mode = "savesdown"; // loads answer, saves still fail: the service's answer arrives first
                yield return WaitUntil(() => _rig.saver.ServerAnswered && !_rig.saver.StartRunning, 15, "the service to answer");
                Assert.AreEqual(1, _rig.testSaver.applied, "the older service save was not applied over the progress made since");
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "the new save to be sent");
                StringAssert.Contains("\\\"v\\\":\\\"local-2\\\"", _svc.NewestStored);
                yield return WaitUntil(() => _rig.saver.StatusText == "", 5, "the Game Saved toast to fade");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T24_NoSaveServiceOnThePage_StartPlaysANewGameWithoutSaving()
        {
            yield return Setup();
            try
            {
                GameSaveManager.saveConfigOverride = () => null;
                GameSaveManager.loadConfigOverride = () => null;
                DatabaseSaver.noServiceConfigStartSeconds = 1f;
                _rig.loadEvent.Raise(0); // START GAME
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("waiting for the save service settings"), 5, "the waiting line");
                yield return WaitUntil(() => _rig.saver.PlayingWithoutService && !_rig.saver.StartRunning, 10, "the decision");
                Assert.AreEqual(1, _rig.saver.NewGamesStarted, "a new game");
                StringAssert.Contains("without saving", _rig.saver.StatusText);
                Assert.IsFalse(_rig.saver.ServerAnswered);
                // Checkpoints are neither kept nor sent on such a page.
                _rig.testSaver.value = "unsaved";
                _rig.saveEvent.Raise(0);
                yield return Wait(0.5f);
                Assert.IsFalse(_rig.saver.HasUnsentSave);
                Assert.AreEqual(0, _svc.SaveCount);
                Assert.IsFalse(PlayerPrefs.HasKey("mhs_last_save"));
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T25_UnreadableNewestSave_TheNextReadableOneIsUsed_AndNoneAtAllIsAFailureNotANewGame()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("readable-older", DateTime.UtcNow.AddMinutes(-10)));
                _svc.stored.Add("{}"); // the newest document: not a save
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "resume from the readable save");
                Assert.AreEqual("readable-older", _rig.testSaver.lastApplied);
                Assert.AreEqual(2, _svc.LoadCount, "one request for the newest, one for the last few");
                lock (_svc.loadBodies) StringAssert.Contains("\"limit\":5", _svc.loadBodies[1]);
                Assert.AreEqual(0, _rig.saver.NewGamesStarted);

                // Only unreadable documents: a failure that is retried, never a new game.
                _svc.stored.Clear();
                _svc.stored.Add("{}");
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 1, 15, "the unreadable answer to count as a failure");
                StringAssert.Contains("unreadable", _rig.saver.StatusText);
                Assert.IsTrue(_rig.saver.StartRunning);
                Assert.AreEqual(0, _rig.saver.NewGamesStarted);
                Assert.AreEqual(0, _rig.testSaver.applied);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T26_RecoveredSaveWaitShowsTheFetchsCountdown_AndTheDeviceCopyLineHoldsAfterStart()
        {
            yield return Setup();
            try
            {
                _svc.mode = "down";
                _rig.testSaver.value = "kept";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                // Relaunch with the service still down: the recovered save waits for the service's answer.
                yield return Launch();
                yield return WaitUntil(() => _rig.saver.HasUnsentSave, 5, "the recovered save");
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 1, 10, "the first failed check");
                yield return Wait(1.2f);
                StringAssert.Contains("Saving an earlier checkpoint", _rig.saver.StatusText);
                StringAssert.Contains("Retrying in", _rig.saver.StatusText);
                // START GAME: the service has already failed, so the game resumes from the unsent
                // copy at once; the line is the device-copy one from then on, and the recovered
                // save's text does not come back over it.
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 5, "resume from the device copy");
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("Using the progress saved on this device"), 3, "the device-copy line");
                for (int i = 0; i < 6; i++)
                {
                    yield return Wait(0.5f);
                    StringAssert.Contains("Using the progress saved on this device", _rig.saver.StatusText);
                }
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave && !_rig.saver.StartRunning, 15, "the recovered save sent and start done");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T28_ANewerSaveDuringTheRetryWaitGoesOutAtOnce_WithItsOwnCount()
        {
            yield return Setup();
            try
            {
                _svc.mode = "down";
                _rig.testSaver.value = "one";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                // The next retry would be 5 s away; a newer save does not wait for it.
                int before = _svc.SaveCount;
                _rig.testSaver.value = "two";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.SaveCount == before + 1, 3, "the newer save's first attempt");
                Assert.AreEqual(1, _rig.saver.SaveAttempt, "its own attempt count");
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "acceptance");
                StringAssert.Contains("\\\"v\\\":\\\"two\\\"", _svc.NewestStored);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T29_ASecondSaverInTheSceneStaysInert()
        {
            yield return Setup();
            try
            {
                // An unsent record in the slot (the service is down): a second saver's Start
                // would otherwise pick it up and ask the service.
                _svc.mode = "down";
                _rig.testSaver.value = "from-the-first";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                int loads = _svc.LoadCount;
                var second = Rig.Build(Uid);
                try
                {
                    yield return Wait(1.5f);
                    Assert.IsTrue(second.saver.IsDuplicate);
                    Assert.IsFalse(_rig.saver.IsDuplicate);
                    second.autoSave.Value = true;
                    second.testSaver.value = "from-the-duplicate";
                    second.saver.SaveGameToDatabase(); // called directly: nothing recorded
                    yield return Wait(0.5f);
                    Assert.IsFalse(second.saver.HasUnsentSave);
                    Assert.AreEqual(loads, _svc.LoadCount, "the duplicate asked the service nothing");
                    lock (_svc.saveBodies) Assert.IsFalse(_svc.saveBodies.Exists(b => b.Contains("from-the-duplicate")), "the duplicate sent nothing");
                    second.saver.CallToTransitionToNextUnit();
                    Assert.IsTrue(second.saver.ReadyToTransition);
                }
                finally { second.Destroy(); }
                // The first one still works.
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "the first saver's save to be accepted");
                Assert.AreEqual(1, _svc.stored.Count);
                StringAssert.Contains("\\\"v\\\":\\\"from-the-first\\\"", _svc.NewestStored);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T30_ACopyTheServiceAcceptedDoesNotBeatTheServicesAnswer()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "device-clock-ahead";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the save to be accepted");
                // The service's newest is an older save (another device's; this device's clock
                // may simply be ahead). The accepted copy does not override the service.
                _svc.stored.Clear();
                _svc.stored.Add(ServerSave("service-says", DateTime.UtcNow.AddMinutes(-5)));
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "start");
                Assert.AreEqual("service-says", _rig.testSaver.lastApplied);
            }
            finally { Cleanup(); }
        }
        // ---- 7. the review of version 1.2 (2026-10-26) ----

        [UnityTest]
        public IEnumerator T31_NoIdentityAndNoConfig_StartPlaysWithoutSaving_AndALeftoverRecordHoldsNoTransition()
        {
            yield return Setup();
            try
            {
                // An unsent record from an earlier launch (the service was down).
                _svc.mode = "down";
                _rig.testSaver.value = "left-over";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");

                // A page with neither identity nor save-service settings (a plain host page).
                GameSaveManager.saveConfigOverride = () => null;
                GameSaveManager.loadConfigOverride = () => null;
                DatabaseSaver.noServiceConfigStartSeconds = 1f;
                _rig.Destroy();
                _rig = Rig.Build("");
                yield return null;
                yield return null;
                _rig.autoSave.Value = true;
                _rig.loadEvent.Raise(0); // START GAME
                yield return WaitUntil(() => _rig.saver.PlayingWithoutService && !_rig.saver.StartRunning, 10, "the decision, with no identity at all");
                Assert.AreEqual(1, _rig.saver.NewGamesStarted);
                StringAssert.Contains("without saving", _rig.saver.StatusText);

                // The same page with an identity: the leftover record is found by the send loop,
                // which makes the same decision without START, and the record holds no transition.
                yield return Launch();
                yield return WaitUntil(() => _rig.saver.HasUnsentSave, 5, "the leftover record");
                yield return WaitUntil(() => _rig.saver.PlayingWithoutService, 5, "the decision made by the send loop");
                _rig.saver.CallToTransitionToNextUnit();
                Assert.IsTrue(_rig.saver.ReadyToTransition, "a record that cannot be sent from this page does not hold the transition");
                yield return WaitUntil(() => _rig.saver.StatusText == "", 5, "the line to clear");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T32_AnOlderSaveAcceptedWhileANewerOneWaitsLeavesTheNewerOneOnTheDevice()
        {
            yield return Setup();
            try
            {
                _svc.mode = "hang"; _svc.hangMs = 3000; // the service answers, slowly
                _rig.testSaver.value = "older";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.SaveCount == 1, 12, "the older save's request to be in flight");
                yield return Wait(0.3f);
                _rig.testSaver.value = "newer";
                _rig.saveEvent.Raise(0); // recorded while the older one is in flight
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                StringAssert.Contains("\\\"v\\\":\\\"newer\\\"", rec.save_json);
                Assert.IsFalse(rec.sent);
                // The older one is accepted and the newer one's request goes out next.
                yield return WaitUntil(() => _svc.SaveCount == 2, 10, "the newer save's request");
                rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                StringAssert.Contains("\\\"v\\\":\\\"newer\\\"", rec.save_json, "the device slot still holds the newer save");
                Assert.IsFalse(rec.sent);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "the newer save to be accepted");
                Assert.AreEqual(2, _svc.stored.Count);
                StringAssert.Contains("\\\"v\\\":\\\"newer\\\"", _svc.NewestStored);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T33_TheLoadLineShowsTheAttemptWhileARequestIsOut()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("server-1", DateTime.UtcNow.AddMinutes(-10)));
                _svc.mode = "down";
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "the first failure");
                _svc.mode = "hang"; _svc.hangMs = 3000; // the next attempt stays in flight for 3 s
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("Loading your progress... (attempt 2)"), 10, "the attempt line during the request");
                StringAssert.DoesNotContain("Retrying in", _rig.saver.StatusText);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "the resume");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T34_PendingIndexEvictionSkipsKeysAlreadyGone_AndAnOversizedRecordStaysInMemory()
        {
            yield return Setup();
            const string A = "aaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbb", C = "cccccccccccccccccccccccc";
            try
            {
                var t = typeof(DatabaseSaver);
                var add = t.GetMethod("AddPendingKey", BindingFlags.NonPublic | BindingFlags.Static);
                var evict = t.GetMethod("EvictOldestPending", BindingFlags.NonPublic | BindingFlags.Static);
                foreach (var id in new[] { A, B, C }) add.Invoke(null, new object[] { id, "{\"user_id\":\"" + id + "\",\"save_json\":\"{}\"}" });
                Assert.AreEqual(A + "," + B + "," + C, PlayerPrefs.GetString("mhs_pending_index"));
                PlayerPrefs.DeleteKey("mhs_pending_save_" + A); // the oldest record vanished (a failed write, an unreadable record)
                Assert.IsTrue((bool)evict.Invoke(null, null), "something was evicted");
                Assert.IsFalse(PlayerPrefs.HasKey("mhs_pending_save_" + B), "the oldest key that was present went");
                Assert.AreEqual(C, PlayerPrefs.GetString("mhs_pending_index"));
                Assert.IsTrue((bool)evict.Invoke(null, null));
                Assert.AreEqual("", PlayerPrefs.GetString("mhs_pending_index"));
                Assert.IsFalse((bool)evict.Invoke(null, null), "nothing left to evict");

                // A record over the ceiling stays in memory only, and is still sent.
                _rig.testSaver.value = new string('x', 140 * 1024);
                _rig.saveEvent.Raise(0);
                yield return null;
                Assert.IsTrue(_rig.saver.HasUnsentSave);
                Assert.IsFalse(PlayerPrefs.HasKey("mhs_last_save"), "not written to the device");
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "still sent to the service");
            }
            finally
            {
                PlayerPrefs.DeleteKey("mhs_pending_save_" + A);
                PlayerPrefs.DeleteKey("mhs_pending_save_" + B);
                PlayerPrefs.DeleteKey("mhs_pending_save_" + C);
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator T35_WhenTheRefetchAtStartFailsTheEarlierAnswerIsUsed()
        {
            yield return Setup();
            try
            {
                // No copy on this device; the menu's answer is old; the service is down at START.
                _svc.stored.Add(ServerSave("menu-answer", DateTime.UtcNow.AddMinutes(-10)));
                typeof(DatabaseSaver).GetMethod("ApplyGameProgressFromDatabase", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_rig.saver, null);
                yield return WaitUntil(() => _rig.saver.ServerAnswered, 10, "the menu's fetch");
                DatabaseSaver.serverAnswerMaxAgeSeconds = 1f;
                yield return Wait(1.5f);
                _svc.mode = "down";
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 10, "resume from the earlier answer");
                Assert.AreEqual("menu-answer", _rig.testSaver.lastApplied);
                Assert.IsTrue(_rig.saver.StartRunning, "still checking");
                _svc.mode = "ok"; // the same save: nothing to apply again
                yield return WaitUntil(() => !_rig.saver.StartRunning, 15, "the refetch to answer");
                Assert.AreEqual(1, _rig.testSaver.applied);
                Assert.AreEqual("", _rig.saver.StatusText);

                // An older accepted copy on this device loses to the earlier answer.
                _rig.testSaver.value = "device-older";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the device copy accepted");
                _svc.stored.Clear();
                _svc.stored.Add(ServerSave("service-newer", DateTime.UtcNow.AddMinutes(5)));
                yield return Launch();
                typeof(DatabaseSaver).GetMethod("ApplyGameProgressFromDatabase", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_rig.saver, null);
                yield return WaitUntil(() => _rig.saver.ServerAnswered, 10, "the menu's fetch");
                DatabaseSaver.serverAnswerMaxAgeSeconds = 1f;
                yield return Wait(1.5f);
                _svc.mode = "down";
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 10, "resume");
                Assert.AreEqual("service-newer", _rig.testSaver.lastApplied, "the earlier answer outranks the accepted device copy");
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.StartRunning, 15, "done");
                Assert.AreEqual(1, _rig.testSaver.applied);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T36_AnUnsentSaveIsDroppedWhenTheServiceIsKnownToHoldNewerProgress()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                _svc.mode = "down";
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                _rig.testSaver.value = "played-on";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.HasUnsentSave, 5, "the new save recorded (held until the service answers)");
                yield return Wait(1.5f);
                Assert.AreEqual(0, _rig.saver.SaveAttempt, "not attempted while START is still deciding");
                // Another device moved past this one meanwhile: the service's newest is newer than the unsent save.
                _svc.stored.Clear();
                _svc.stored.Add(ServerSave("elsewhere-newer", DateTime.UtcNow.AddMinutes(5)));
                _svc.mode = "savesdown";
                yield return WaitUntil(() => _rig.testSaver.applied == 2 && !_rig.saver.StartRunning, 15, "the newer save applied in place");
                Assert.AreEqual("elsewhere-newer", _rig.testSaver.lastApplied);
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "the unsent save decided");
                yield return Wait(0.5f);
                Assert.AreEqual(1, _svc.stored.Count, "the older unsent save was dropped, not sent over the newer progress");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.dropped);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T37_ASaverWithoutReferencesStaysInertAndNeverTakesCharge()
        {
            yield return Setup();
            try
            {
                // No saver in charge: the stray enables first (the order V7 is about).
                _rig.Destroy();
                _rig = null;
                yield return null;
                var stray = new GameObject("StrayLoadButton");
                try
                {
                    var saver = stray.AddComponent<DatabaseSaver>(); // no references, no GameSaveManager
                    yield return null;
                    Assert.IsTrue(saver.IsDuplicate, "inert although nothing else is in charge");
                    saver.CallToTransitionToNextUnit();
                    Assert.IsTrue(saver.ReadyToTransition);
                    // The real saver that comes up afterwards takes charge.
                    yield return Launch();
                    Assert.IsFalse(_rig.saver.IsDuplicate, "the real one is in charge");
                    _rig.testSaver.value = "still-works";
                    _rig.saveEvent.Raise(0);
                    yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the real saver's save accepted");
                }
                finally { UnityEngine.Object.DestroyImmediate(stray); }
            }
            finally { Cleanup(); }
        }
        [UnityTest]
        public IEnumerator T38_ASaveOfThisLaunchIsNotDroppedOnTheServersClockAlone()
        {
            yield return Setup();
            try
            {
                // The service's newest is an old build's save (no metadata), whose server-clock
                // time is three minutes ahead of this device's clock. A save made in this launch
                // still goes out: a server-clock comparison alone never drops it.
                var old = new SavedGameData { sceneName = SceneManager.GetActiveScene().name };
                old.SetData("SaveFixTestSaver", -1, "{\"v\":\"old-build\"}");
                _svc.stored.Add(JsonUtility.ToJson(old));
                _svc.loadTimestamp = DateTime.UtcNow.AddMinutes(3).ToString("o");
                typeof(DatabaseSaver).GetMethod("ApplyGameProgressFromDatabase", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_rig.saver, null);
                yield return WaitUntil(() => _rig.saver.ServerAnswered, 10, "the menu's fetch");
                _rig.testSaver.value = "this-launch";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the save to be decided");
                Assert.AreEqual(2, _svc.stored.Count, "sent, not dropped");
                StringAssert.Contains("\\\"v\\\":\\\"this-launch\\\"", _svc.NewestStored);
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.sent);
                Assert.IsFalse(rec.dropped);
            }
            finally { Cleanup(); }
        }
        // ---- 8. the review of version 1.3 (2026-10-26) ----

        [UnityTest]
        public IEnumerator T39_AServiceSaveWithAClockAheadOfThisDeviceDoesNotStopThisLaunchsCheckpoints()
        {
            yield return Setup();
            try
            {
                // Made on another device whose clock runs three minutes fast.
                _svc.stored.Add(ServerSave("fast-clock", DateTime.UtcNow.AddMinutes(3)));
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "resume from it");
                _rig.testSaver.value = "played-here";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the checkpoint to be decided");
                Assert.AreEqual(2, _svc.stored.Count, "sent, not dropped");
                StringAssert.Contains("\\\"v\\\":\\\"played-here\\\"", _svc.NewestStored);
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.sent);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T40_ACheckpointMadeWhileTheServiceIsFurtherAlongElsewhereIsDroppedAndSaidSo()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("here", DateTime.UtcNow.AddMinutes(-10)));
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "resume from the service's save");
                // The tab idles; the service's newest moves on to another unit (the student played
                // elsewhere). At START the refetch fails first and the game resumes its earlier answer.
                DatabaseSaver.serverAnswerMaxAgeSeconds = 1f;
                yield return Wait(1.5f);
                _svc.stored.Add(ServerSave("elsewhere", DateTime.UtcNow, "Unit 9 Dev"));
                _svc.mode = "down";
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 2, 10, "resume from the earlier answer");
                _svc.mode = "savesdown"; // the refetch answers with the other unit's save; saves still fail
                yield return WaitUntil(() => !_rig.saver.StartRunning, 15, "the reconcile");
                StringAssert.Contains("another device", _rig.saver.StatusText);
                Assert.AreEqual(2, _rig.testSaver.applied, "the other unit's save is not applied here");
                // A checkpoint here is newer by the clock than the other unit's save, and is still not sent.
                _svc.mode = "ok";
                _rig.testSaver.value = "played-here";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the checkpoint to be decided");
                Assert.AreEqual(2, _svc.stored.Count, "dropped, not sent over the further progress");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.dropped);
                StringAssert.Contains("Not saved", _rig.saver.StatusText);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T41_APartialConfigIsWaitedOn_NotTakenForNoService()
        {
            yield return Setup();
            try
            {
                // An unsent record from an earlier launch.
                _svc.mode = "down";
                _rig.testSaver.value = "left-over";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                // A page with a load endpoint but no save endpoint.
                GameSaveManager.saveConfigOverride = () => null;
                DatabaseSaver.noServiceConfigStartSeconds = 1f;
                _svc.mode = "ok";
                yield return Launch();
                yield return WaitUntil(() => _rig.saver.HasUnsentSave, 5, "the leftover record");
                yield return Wait(2.5f);
                Assert.IsFalse(_rig.saver.PlayingWithoutService, "one endpoint present: a partial setup to wait on, not 'no service'");
                StringAssert.Contains("settings are incomplete", _rig.saver.StatusText);
                // START: the service answers (nothing stored there); the unsent copy is resumed, no new game.
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "resume from the unsent copy");
                Assert.AreEqual(0, _rig.saver.NewGamesStarted);
                Assert.IsFalse(_rig.saver.PlayingWithoutService);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T42_ASaveRecordedWithNoIdentityAndNoSettingsIsKeptQuietly()
        {
            yield return Setup();
            try
            {
                GameSaveManager.saveConfigOverride = () => null;
                GameSaveManager.loadConfigOverride = () => null;
                DatabaseSaver.noServiceConfigStartSeconds = 1f;
                _rig.Destroy();
                _rig = Rig.Build("");
                yield return null;
                yield return null;
                _rig.autoSave.Value = true;
                _rig.testSaver.value = "no-page";
                _rig.saveEvent.Raise(0);
                Assert.IsTrue(_rig.saver.HasUnsentSave);
                // The settings are decided before the id is waited for: no line stays up for ever.
                yield return WaitUntil(() => _rig.saver.PlayingWithoutService, 5, "the decision made by the send loop, with no identity");
                yield return WaitUntil(() => _rig.saver.StatusText == "", 5, "the line to clear");
                _rig.saver.CallToTransitionToNextUnit();
                Assert.IsTrue(_rig.saver.ReadyToTransition);
            }
            finally { Cleanup(); }
        }
        // ---- 9. the focused review of the base logic (2026-10-26) ----

        [UnityTest]
        public IEnumerator T43_WithNoBaseAdoptedAtTheMenu_ARefusedAnswerIsNotAdoptedBySaving()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                // The menu's fetch fails once with a copy on the device: the menu adopts nothing.
                _svc.mode = "down";
                yield return Launch();
                typeof(DatabaseSaver).GetMethod("ApplyGameProgressFromDatabase", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_rig.saver, null);
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 1, 10, "the menu's failed fetch");
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 10, "resume from the device copy");
                // The service answers with newer progress in another unit: refused, not adopted.
                _svc.stored.Clear();
                _svc.stored.Add(ServerSave("elsewhere", DateTime.UtcNow.AddMinutes(1), "Unit 9 Dev"));
                _svc.mode = "savesdown";
                yield return WaitUntil(() => !_rig.saver.StartRunning, 15, "the reconcile");
                StringAssert.Contains("another device", _rig.saver.StatusText);
                _svc.mode = "ok";
                _rig.testSaver.value = "played-here";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the checkpoint decided");
                Assert.AreEqual(1, _svc.stored.Count, "dropped: the refused answer was not adopted by the send loop");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.dropped);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T44_ACheckpointDuringTheRefetchWaitsAndIsSentWhenTheSameSaveComesBack()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("menu-answer", DateTime.UtcNow.AddMinutes(-10)));
                typeof(DatabaseSaver).GetMethod("ApplyGameProgressFromDatabase", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_rig.saver, null);
                yield return WaitUntil(() => _rig.saver.ServerAnswered, 10, "the menu's fetch");
                DatabaseSaver.serverAnswerMaxAgeSeconds = 1f;
                yield return Wait(1.5f); // past the age limit (the menu applies nothing through this saver, so no replay window is in play)
                _svc.mode = "hang"; _svc.hangMs = 2000; // the refetch stays in flight for 2 s
                _rig.loadEvent.Raise(0);
                yield return Wait(0.3f);
                _rig.testSaver.value = "held";
                _rig.saveEvent.Raise(0); // while START is still deciding
                yield return Wait(0.5f);
                Assert.AreEqual(0, _rig.saver.SaveAttempt, "held while START decides");
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave && !_rig.saver.StartRunning, 15, "the same save comes back and the checkpoint goes out");
                Assert.AreEqual(2, _svc.stored.Count, "sent, not dropped: the same save is not 'newer progress'");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.sent);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T45_ASaveSupersededWhileInFlightIsFollowedByTheCurrentState()
        {
            yield return Setup();
            try
            {
                _svc.stored.Add(ServerSave("s1", DateTime.UtcNow.AddMinutes(-10)));
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "resume from s1");
                _svc.mode = "saveshang"; _svc.hangMs = 3000; // the checkpoint's request stays in flight
                _rig.testSaver.value = "c";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.SaveCount == 1, 5, "the checkpoint's request in flight");
                // Meanwhile the service's newest moved on (same scene); START's refetch applies it in place.
                DatabaseSaver.serverAnswerMaxAgeSeconds = 0.01f;
                _svc.stored.Add(ServerSave("s2", DateTime.UtcNow.AddMinutes(1)));
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 2 && _rig.testSaver.lastApplied == "s2", 10, "the newer save applied in place");
                // The in-flight checkpoint is accepted afterwards (the third stored document, after
                // s1 and s2); the current state is then saved over it (the fourth).
                yield return WaitUntil(() => _svc.stored.Count == 4, 15, "the current state saved over the superseded one");
                StringAssert.Contains("\\\"v\\\":\\\"s2\\\"", _svc.NewestStored);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 5, "done");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.sent);
                Assert.IsFalse(rec.dropped);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T46_ASaveEndpointWithoutALoadEndpointWaitsVisibly_NoNewGame()
        {
            yield return Setup();
            try
            {
                GameSaveManager.loadConfigOverride = () => null;
                DatabaseSaver.noServiceConfigStartSeconds = 1f;
                _rig.loadEvent.Raise(0);
                yield return Wait(3f);
                Assert.IsFalse(_rig.saver.PlayingWithoutService);
                Assert.AreEqual(0, _rig.saver.NewGamesStarted, "a partial setup never starts a new game");
                Assert.IsTrue(_rig.saver.StartRunning);
                StringAssert.Contains("incomplete", _rig.saver.StatusText);
            }
            finally { Cleanup(); }
        }
        // ---- 10. the review of the base logic's second pass (2026-10-26) ----

        [UnityTest]
        public IEnumerator T47_ARecordDroppedAsSupersededIsNeverResumedAgain()
        {
            yield return Setup();
            try
            {
                // Make a superseded record: resume from a copy, play on, the service turns out further along.
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                _svc.mode = "down";
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                yield return Wait(5.5f); // beyond the replay window: the next checkpoint is progress made since the resume
                _rig.testSaver.value = "played-on";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.HasUnsentSave, 5, "the new save recorded");
                _svc.stored.Clear();
                _svc.stored.Add(ServerSave("further", DateTime.UtcNow.AddMinutes(5)));
                _svc.mode = "savesdown";
                yield return WaitUntil(() => _rig.testSaver.applied == 2 && !_rig.saver.StartRunning, 15, "the further save applied in place");
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the superseded save decided");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.dropped && rec.superseded);

                // The next launch: with the service answering, its save; with it down, no copy to resume from.
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && !_rig.saver.StartRunning, 15, "start");
                Assert.AreEqual("further", _rig.testSaver.lastApplied, "the superseded record did not come back");
                _svc.mode = "down";
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.FetchFailures >= 2, 15, "two failed attempts");
                Assert.AreEqual(0, _rig.testSaver.applied, "no device copy to resume from");
                StringAssert.Contains("Can't load your progress yet", _rig.saver.StatusText);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T48_ACheckpointHeldDuringTheResumeShowsTheDeviceCopyCountdown()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                _svc.mode = "down";
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                _rig.testSaver.value = "played-on";
                _rig.saveEvent.Raise(0);
                yield return Wait(2f);
                Assert.AreEqual(0, _rig.saver.SaveAttempt, "held while START decides");
                StringAssert.Contains("Still checking the save service", _rig.saver.StatusText);
                StringAssert.DoesNotContain("Saving Game", _rig.saver.StatusText, "a held save shows no save line of its own");
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("attempt"), 5, "the device-copy countdown with the attempt count");
                Assert.AreEqual(0, _rig.saver.SaveAttempt, "still held");
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave && !_rig.saver.StartRunning, 15, "the checkpoint decided once the service answers");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T49_TheCheckpointAnApplyReFiresIsAReplay_NotProgressOverNewerSavesElsewhere()
        {
            yield return Setup();
            try
            {
                // An unsent save R from an earlier launch (the service was down)...
                _svc.mode = "down";
                _rig.testSaver.value = "R";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1 && _rig.saver.StatusText.Contains("Retrying in"), 10, "first attempt to be answered");
                DateTime afterR = DateTime.UtcNow.AddMilliseconds(200);
                yield return Wait(0.5f);
                // ...the student then played further on another device (B, newer than R)...
                string b = ServerSave("B", afterR);
                // ...and this device launches with the service still unreachable: resume from R, which re-fires the checkpoint.
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1 && _rig.testSaver.lastApplied == "R", 15, "resume from R");
                _rig.saveEvent.Raise(0); // the re-fire: the same state, a fresh clock
                yield return Wait(0.3f);
                // The service answers with B: newer than R, older by the clock than the re-fired record.
                _svc.stored.Add(b);
                _svc.mode = "savesdown";
                yield return WaitUntil(() => _rig.testSaver.applied == 2 && !_rig.saver.StartRunning, 15, "B applied in place over the replayed R");
                Assert.AreEqual("B", _rig.testSaver.lastApplied);
                _svc.mode = "ok";
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the replayed record decided");
                Assert.AreEqual(1, _svc.stored.Count, "nothing of R's state was sent over B");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.dropped && rec.superseded);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T50_AfterTheHoldRunsOutTheOwnSaveGoesOutAndIsNotTakenForNewerProgress()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                // Loads fail, saves work (a load-only outage); the hold is short for the test.
                DatabaseSaver.heldSaveMaxSeconds = 2f;
                _svc.loadsDown = true;
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                _rig.saveEvent.Raise(0); // the apply's re-fire: the resumed state with a fresh clock
                yield return Wait(0.5f);
                Assert.AreEqual(0, _rig.saver.SaveAttempt, "held at first");
                StringAssert.DoesNotContain("Saving Game", _rig.saver.StatusText, "a held save shows no save line of its own");
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the hold runs out and the save is accepted");
                Assert.AreEqual(2, _svc.stored.Count);
                yield return WaitUntil(() => !_rig.saver.StartRunning, 5, "START ends on the launch's own save");
                yield return Wait(1f);
                Assert.AreEqual(1, _rig.testSaver.applied, "the own save (a replay of the copy, newer by the clock) is not applied back as newer progress");
                // The fetch's countdown ends with the own save known as the newest, and the line clears after the toast.
                yield return WaitUntil(() => _rig.saver.StatusText == "", 6, "the line clears");
                yield return Wait(1f);
                Assert.AreEqual("", _rig.saver.StatusText, "no fetch countdown lingers");
                // Later saves of this START go out at once, with the loads still down.
                _rig.testSaver.value = "more";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.stored.Count == 3, 3, "the next save goes out without a hold");
                // A second START in the same launch decides afresh: its checkpoints are held again.
                DatabaseSaver.serverAnswerMaxAgeSeconds = 0.01f;
                yield return Wait(0.1f);
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 2, 15, "the second START resumes from the earlier answer");
                _rig.testSaver.value = "more2";
                _rig.saveEvent.Raise(0);
                yield return Wait(0.5f);
                Assert.AreEqual(3, _svc.stored.Count, "held again while the second START decides");
                yield return WaitUntil(() => _svc.stored.Count == 4, 10, "sent once the second hold runs out");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T51_ASaveAcceptedAfterSTARTRefusedFurtherProgressDoesNotLiftTheRefusal_TheFurtherProgressIsPostedAgain()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                DatabaseSaver.heldSaveMaxSeconds = 2f;
                _svc.loadsDown = true;
                _svc.saveSleepMs = 20000; // the save sent after the hold stays in flight while START decides
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                yield return Wait(5.5f);
                _rig.testSaver.value = "played-on";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.SaveCount == 2, 10, "the save goes out after the hold (in flight)");
                _svc.saveSleepMs = 0; // only that request is slow
                // Meanwhile the student is further along in another unit; the loads come back.
                _svc.stored.Add(ServerSave("elsewhere", DateTime.UtcNow.AddMinutes(5), "Another Unit"));
                _svc.loadsDown = false;
                yield return WaitUntil(() => !_rig.saver.StartRunning, 15, "START decides");
                StringAssert.Contains("another device", _rig.saver.StatusText);
                Assert.AreEqual(1, _rig.testSaver.applied, "the player stays where they are");
                // The in-flight save is accepted afterwards (the service's newest for a moment); the
                // refusal stands: the further progress is posted again, and nothing else of this session is sent.
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 30, "the in-flight save accepted");
                yield return WaitUntil(() => _svc.stored.Count == 4, 10, "the further progress posted again");
                StringAssert.Contains("\\\"v\\\":\\\"elsewhere\\\"", _svc.NewestStored);
                _rig.testSaver.value = "more";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the next checkpoint decided");
                Assert.AreEqual(4, _svc.stored.Count, "not sent over the further progress");
                StringAssert.Contains("Not saved: the save service has newer progress", _rig.saver.StatusText);
            }
            finally { Cleanup(); }
        }

        [Test]
        public void T52_FurtherAlongOrdersUnitsByNumberAndFallsBackToTheSceneName()
        {
            var m = typeof(DatabaseSaver).GetMethod("FurtherAlong", BindingFlags.NonPublic | BindingFlags.Static);
            bool F(string scene, string here) => (bool)m.Invoke(null, new object[] { scene, here });
            Assert.IsTrue(F("Unit 3 Dev", "Unit 2 Dev"), "a later unit is further on");
            Assert.IsFalse(F("Unit 1 Dev", "Unit 2 Dev"), "an earlier unit is not");
            Assert.IsFalse(F("Unit 2 Dev", "Unit 2 Dev"), "the same scene is not");
            Assert.IsTrue(F("Unit 4 Dev - Dungeon", "Unit 4 Dev"), "another scene of the same unit is taken as further on (its order is not known here)");
            Assert.IsTrue(F("Unit 2 Dev", "MainMenu"), "without a number on one side, another scene counts as further");
            Assert.IsTrue(F("Another Unit", "Scene"), "without numbers, another scene counts as further");
            Assert.IsFalse(F("Scene", "Scene"), "without numbers, the same scene does not");
        }

        [UnityTest]
        public IEnumerator T55_ASecondSTARTAfterARefusal_WithTheLoadsDown_HoldsItsSavesAndSendsThemWhenTheHoldRunsOut()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                DatabaseSaver.heldSaveMaxSeconds = 2f;
                _svc.loadsDown = true;
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                _svc.stored.Add(ServerSave("elsewhere", DateTime.UtcNow.AddMinutes(5), "Another Unit"));
                _svc.loadsDown = false;
                yield return WaitUntil(() => !_rig.saver.StartRunning, 20, "START refuses the further progress");
                yield return WaitUntil(() => _rig.saver.StatusText.Contains("another device"), 5, "the refusal said");
                // Back at the menu, START again after the age limit with the loads down again.
                _svc.loadsDown = true;
                DatabaseSaver.serverAnswerMaxAgeSeconds = 0.01f;
                yield return Wait(2.5f);
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 2, 15, "the second START resumes from the earlier answer");
                yield return Wait(5.5f);
                int before = _svc.stored.Count;
                _rig.testSaver.value = "second";
                _rig.saveEvent.Raise(0);
                yield return Wait(0.5f);
                Assert.AreEqual(before, _svc.stored.Count, "held while the second START decides, not dropped by the earlier refusal");
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 15, "sent once the hold runs out");
                Assert.AreEqual(before + 1, _svc.stored.Count);
                lock (_logs) foreach (var l in _logs) StringAssert.DoesNotContain("NullReferenceException", l);
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T56_AnUnsentCopyAndANoSavesAnswerAfterALocalResume_TheSessionStillSaves()
        {
            yield return Setup();
            try
            {
                // A copy the service never accepted (saves and loads down), and a launch while still down.
                _svc.mode = "down";
                _rig.testSaver.value = "only-here";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.SaveAttempt >= 1, 10, "first attempt answered");
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the unsent copy");
                _svc.mode = "ok"; // the service answers: no saves
                yield return WaitUntil(() => !_rig.saver.StartRunning, 20, "START decides");
                StringAssert.DoesNotContain("cleared", _rig.saver.StatusText);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 20, "the unsent copy goes out");
                Assert.AreEqual(1, _svc.stored.Count, "progress that exists only here is saved");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T53_SavesClearedWhileTheLoadsWereDown_TheSessionIsNotSavedAndTheNextLaunchStartsAnew()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                _svc.loadsDown = true;
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                _svc.stored.Clear(); // the saved progress is cleared (staff) while this device cannot load
                yield return Wait(5.5f);
                _rig.testSaver.value = "played-on";
                _rig.saveEvent.Raise(0); // held while START decides
                _svc.loadsDown = false;  // the service answers: no saves
                yield return WaitUntil(() => !_rig.saver.StartRunning, 15, "START decides");
                StringAssert.Contains("saved progress was cleared", _rig.saver.StatusText);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the held checkpoint decided");
                Assert.AreEqual(0, _svc.stored.Count, "nothing of this session recreates the cleared progress");
                var rec = JsonUtility.FromJson<DatabaseSaver.LocalSaveRecord>(PlayerPrefs.GetString("mhs_last_save"));
                Assert.IsTrue(rec.dropped && rec.superseded);
                // The next launch, with the service answering: a new game, not the device's copy.
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.saver.NewGamesStarted == 1 && !_rig.saver.StartRunning, 15, "a new game");
                Assert.AreEqual(0, _rig.testSaver.applied, "no device copy resumed");
            }
            finally { Cleanup(); }
        }

        [UnityTest]
        public IEnumerator T54_ASecondSTARTAfterARefusalResumesTheRefusedProgress_NotTheSaveAcceptedSince()
        {
            yield return Setup();
            try
            {
                _rig.testSaver.value = "copy";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 10, "the copy accepted");
                DatabaseSaver.heldSaveMaxSeconds = 2f;
                _svc.loadsDown = true;
                _svc.saveSleepMs = 20000;
                yield return Launch();
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 1, 15, "resume from the device copy");
                yield return Wait(5.5f);
                _rig.testSaver.value = "played-on";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.SaveCount == 2, 10, "the save goes out after the hold (in flight)");
                _svc.saveSleepMs = 0;
                _svc.stored.Add(ServerSave("elsewhere", DateTime.UtcNow.AddMinutes(5), "Another Unit"));
                _svc.loadsDown = false;
                yield return WaitUntil(() => !_rig.saver.StartRunning, 15, "START refuses the further progress");
                _svc.mode = "savesdown"; // the further progress cannot be posted again yet
                yield return WaitUntil(() => !_rig.saver.HasUnsentSave, 30, "the in-flight save accepted");
                Assert.AreEqual(3, _svc.stored.Count, "copy, elsewhere, and the stale save accepted after the refusal");
                // Back at the menu, START again after the answer's age limit: the service's newest is the stale save,
                // but this launch decides on the refused further progress.
                DatabaseSaver.serverAnswerMaxAgeSeconds = 0.01f;
                yield return Wait(0.1f);
                _rig.loadEvent.Raise(0);
                yield return WaitUntil(() => _rig.testSaver.applied == 2 && !_rig.saver.StartRunning, 15, "the second START resumes");
                Assert.AreEqual("elsewhere", _rig.testSaver.lastApplied, "the further progress, not the stale save");
                // The re-post goes on while the further progress is the adopted base, so the stale save is not the service's newest.
                _svc.mode = "ok";
                yield return WaitUntil(() => _svc.stored.Count == 4, 15, "the further progress posted again");
                StringAssert.Contains("\\\"v\\\":\\\"elsewhere\\\"", _svc.NewestStored);
                yield return Wait(6.5f);
                Assert.AreEqual(4, _svc.stored.Count, "posted once, then the re-post ends");
                _rig.testSaver.value = "after";
                _rig.saveEvent.Raise(0);
                yield return WaitUntil(() => _svc.stored.Count == 5, 10, "the session saves from the further progress");
                StringAssert.Contains("\\\"v\\\":\\\"after\\\"", _svc.NewestStored);
            }
            finally { Cleanup(); }
        }
    }
}
#endif
