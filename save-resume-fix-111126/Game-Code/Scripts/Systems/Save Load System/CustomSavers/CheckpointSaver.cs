using Obvious.Soap;
using PixelCrushers;
using PixelCrushers.DialogueSystem;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;


namespace MHS
{
    [AddComponentMenu("Pixel Crushers/Save System/Savers/Checkpoint Saver")]
    public class CheckpointSaver : Saver
    {
        [Serializable]
        public class CheckpointData
        {
            public int id;

            public CheckpointData(int id)
            {
                this.id = id;
            }
        }

        [SerializeField] private ScriptableEventQuestEntryData _questEntryEvent;
        [SerializeField] private QuestAndUnitSO _questAndUnitSO;

        [SerializeField] private ScriptableEventInt updateSceneCheckpoint;
        [SerializeField] private IntVariable sceneCheckpoint;
        [Tooltip("Fill this list with checkpoints that should ONLY fire if they are the current checkpoint the player is at.")]
        [SerializeField] private List<int> sceneCheckpointSkipList;


        [Tooltip("This will fire events in sequential order up to the index of the list equal to the scene checkpoint value")]
        [SerializeField] private List<UnityEvent> events;

        [SerializeField] private bool printDebug = true;


        // True only while ApplySaveCheckpointFirst has applied this saver and the save system's own pass is running.
        private bool _appliedAhead;

        /// <summary>
        /// Applies a save with the checkpoint replay first and every other saver after it, so what the savers recorded
        /// is what the player gets back. SaveSystem keeps its savers in a HashSet, so its own order is undefined, and the
        /// replay switches objects that other savers own (Unit 3 supply crate, Unit 4 puzzle collider). Use this instead
        /// of SaveSystem.ApplySavedGameData wherever the game applies a save.
        /// </summary>
        public static void ApplySaveCheckpointFirst(SavedGameData saveData)
        {
            var ahead = new List<CheckpointSaver>();
            if (saveData != null)
            {
                foreach (var cs in FindObjectsByType<CheckpointSaver>(FindObjectsSortMode.None))
                {
                    if (!cs.isActiveAndEnabled) continue; // only savers the save system has registered
                    // Applied once either way: as in SaveSystem's own loop, an exception is logged and the rest goes on.
                    try { cs.ApplyData(saveData.GetData(cs.key)); }
                    catch (Exception e) { Debug.LogException(e); }
                    cs._appliedAhead = true;
                    ahead.Add(cs);
                }
            }
            try
            {
                SaveSystem.ApplySavedGameData(saveData);
            }
            finally
            {
                foreach (var cs in ahead) if (cs != null) cs._appliedAhead = false;
            }
        }

        public override void Start()
        {
            base.Start();
            updateSceneCheckpoint.OnRaised += SetSceneCheckpoint;

            UnityEngine.Assertions.Assert.IsNotNull(_questAndUnitSO,"Quest and Unit SO cannot be null.  Required to reset quests outside of this unit.");
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            updateSceneCheckpoint.OnRaised -= SetSceneCheckpoint;
        }

        protected CheckpointData m_Data;
        public override string RecordData()
        {
            CheckpointData data = new CheckpointData(sceneCheckpoint.Value);

            m_Data = data;

            return SaveSystem.Serialize(data);

        }

        public override void ApplyData(string s)
        {
            if (_appliedAhead) return; // already applied by ApplySaveCheckpointFirst

            var data = SaveSystem.Deserialize(s, m_Data);
            sceneCheckpoint.Value = data.id;

            if (sceneCheckpoint.Value > -1)
            {
                for (int i = 0; i <= sceneCheckpoint.Value; i++)
                {
                    if (sceneCheckpoint.Value > i && sceneCheckpointSkipList.Contains(i))
                    {
                        ConsoleLogManager.Instance.PrintToConsole("Skipping event: " + i, printDebug);
                        continue;
                    }
                    if (events[i] != null)
                        events[i].Invoke();
                }
            }

            StartCoroutine(WaitToApplyWaypoints());

        }

        private void ApplyWaypoints()      
        {
            //Save a Dictionary of all Active Quests and their associated Quest Entries
        
            //Check for tracking on each quest
            //If the quests are not from this unit set the main quest state to

            //Apply the quests and quest entries to questEntryEvent in order.

            //Check if the Quest belongs to this unit. If they do reset them.
            
            
            if(_questAndUnitSO == null)
            {
                Debug.LogError("QuestAndUnitSO cannot be null. Needed to set unfinished quests in prior units to Unassigned");
                return;
            }
            // resetQuest.ResetUnfinishedQuests();

            string[] activeQuests = QuestLog.GetAllQuests(QuestState.Active);

            Dictionary<string, int> activeQuestsAndEntries = new Dictionary<string, int>();
            Dictionary<string, int> questsAndEntriesToReset = new Dictionary<string, int>();
            Unit currentUnit = (Unit)SceneLoadingUtilities.GetUnitNumberForThisBuild();

            foreach(string quest in activeQuests)
            {
                if(QuestLog.IsQuestTrackingEnabled(quest) == false)
                {
                    ConsoleLogManager.Instance.PrintToConsole($"{quest} does not have tracking enabled, skipping load state", printDebug);
                    continue;
                }            

                int questEntryCount = QuestLog.GetQuestEntryCount(quest);
                Unit questUnitNumber = QuestResetUtility.GetUnitNumberFromQuest(quest, _questAndUnitSO.QuestsByUnit);
                for(int i = 0; i <= questEntryCount; i++)
                {
                    
                    if(QuestLog.GetQuestEntryState(quest, i) == QuestState.Active && QuestLog.IsQuestTrackingEnabled(quest) == true)
                    {
                        if(questUnitNumber != currentUnit)
                        {
                            //If outside of this unit, set quest to unassigned.
                            ConsoleLogManager.Instance.PrintToConsole($"Quest: {quest} entry {QuestLog.GetQuestEntry(quest, i)} belongs to {questUnitNumber} and will be set to Unassigned");
                            questsAndEntriesToReset.Add(quest,i);
                        }
                        else
                        {
                            activeQuestsAndEntries.Add(quest, i);                            
                        }
                    }
                }
            }

            //For quests that are outside of this unit, reset them to unassigned
            foreach(KeyValuePair<string, int> keyValue in questsAndEntriesToReset)
            {
                QuestLog.SetQuestEntryState(keyValue.Key, keyValue.Value, QuestState.Unassigned);
                QuestLog.SetQuestState(keyValue.Key, QuestState.Unassigned);
                QuestEntryData quest = new(keyValue.Key, keyValue.Value, QuestState.Unassigned);
                ConsoleLogManager.Instance.PrintToConsole($"Resetting to Unnassigned and Raising Quest Entry Event on Quest: {keyValue.Key}, Entry: {QuestLog.GetQuestEntry(keyValue.Key, keyValue.Value)}", printDebug);
                _questEntryEvent.Raise(quest);
            }

            //For active quests in this unit, activate them
            foreach(KeyValuePair<string, int> keyValuePair in activeQuestsAndEntries)
            {
                //Send quest entry event
                QuestEntryData quest = new(keyValuePair.Key, keyValuePair.Value, QuestState.Active);
                ConsoleLogManager.Instance.PrintToConsole($"Raising Quest Entry Event on Quest: {keyValuePair.Key}, Entry: {QuestLog.GetQuestEntry(keyValuePair.Key, keyValuePair.Value)}", printDebug);
                _questEntryEvent.Raise(quest);
            }

            // string activeQuest = "";
            // foreach (string aq in activeQuests)
            // {
            //     if (QuestLog.IsQuestTrackingEnabled(aq))
            //     {
            //         //This is a flaw, if two trackable quests are Active then only the last one will be cached
            //         activeQuest = aq;
            //     }
            // }
            // ConsoleLogManager.Instance.PrintToConsole("aq: " + activeQuest, printDebug);
            // int questCount = QuestLog.GetQuestEntryCount(activeQuest);
            // List<int> questEntryNum = new();

            // ConsoleLogManager.Instance.PrintToConsole("entryCount: " + questCount, printDebug);

            // for (int i = 0; i <= questCount; i++)
            // {
            //     //This checks for active quest entires within the cached activeQuest and adds the quest entry in to a list of integers
            //     if (QuestLog.GetQuestEntryState(activeQuest, i) == QuestState.Active)
            //     {
            //         questEntryNum.Add(i);
            //     }
            // }

            // foreach (int q in questEntryNum)
            // {
            //     ConsoleLogManager.Instance.PrintToConsole(q.ToString(), printDebug);
            //     foreach (var aQuest in activeQuests)
            //     {
            //         //Instead of using the single cached activeQuest this cycles through all the active quests but
            //         //doesn't check to make sure the quest entries in questEntryNum are associated with the quest.  
            //         //This is why when the game is reloaded quest entries can be activated that were not before. 
            //         QuestEntryData quest = new(aQuest, q, QuestState.Active);
            //         _questEntryEvent.Raise(quest);
            //     }
            // }
        }

        private IEnumerator WaitToApplyWaypoints()
        {
            yield return new WaitForEndOfFrame();
            ApplyWaypoints();

        }

        public void SetSceneCheckpoint(int id)
        {
            sceneCheckpoint.Value = id;
        }

        public int GetSavedCheckpointValue(SavedGameData savedGameData)
        {
            //Get the checkpoint data from the cached game save data
            string savedCheckpointData = savedGameData.GetData(key);

            //Or we can use the data directly after deserializing ourselves
            var data = SaveSystem.Deserialize(savedCheckpointData, m_Data);

            //Save data does not correspond with the current Unit
            if (string.IsNullOrEmpty(savedCheckpointData) == true)
            {
                ConsoleLogManager.Instance.PrintToConsole($"Unable to retrieve saved checkpoint data with given key. Saved Data last saved in {savedGameData.sceneName}." +
                "Starting Player at the beginning of this Unit.", printDebug, ConsoleLogManager.ConsoleLogType.Error);
                return -1;
            }

            return data.id;
        }
    }
}
