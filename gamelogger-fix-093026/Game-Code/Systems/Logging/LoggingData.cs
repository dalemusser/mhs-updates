using Obvious.Soap;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHS
{
    [CreateAssetMenu(fileName = "LoggingData", menuName = "MHS/Data/LoggingData")]
    public class LoggingData : ScriptableObject
    {
        [SerializeReference] public List<LoggingDataEntry> entryField;

        private void OnEnable()
        {
            if (entryField == null)
            {
                entryField = new List<LoggingDataEntry>();
            }
        }

        public virtual LoggingDataEntry GetEventKey(string valueOfKey)
        {
            foreach(LoggingDataEntry entry in entryField)
            {
                if (entry.GetEventKey(valueOfKey) != null)
                {
                    return entry;
                }
            }
            Debug.LogWarning($"No entry field with key: {valueOfKey}");
            return null;
        }

        public virtual LoggingDataEntry GetEventType(string eventType)
        {
            foreach (LoggingDataEntry entry in entryField)
            {
                if (entry.eventType == eventType)
                {
                    return entry;
                }
            }

            Debug.LogWarning($"No entry field with event type: {eventType}");
            return null;
        }
    }

    [Serializable]
    public abstract class LoggingDataEntry
    {
        [Tooltip("The type of event that is being logged: eg 'DialogueEvent', etc.")]
        public string eventType;

        [Serializable]
        public struct SpecificEventInformation
        {
            [Tooltip("The type of event specific to what is being logged: eg 'dialogueSpecificEvent', etc.")]
            public string Key;

            [Tooltip("The type of event specific to what is being logged: eg 'dialogueStartEvent', etc.")]
            public string Value;
        }

        [Tooltip("Specific event information in a key/value pair.")]
        [SerializeField] protected List<SpecificEventInformation> specificEventInformation;

        public void OnEnable()
        {
            foreach(var info in specificEventInformation)
            {
                if (string.IsNullOrEmpty(eventType) || string.IsNullOrEmpty(info.Key))
                {
                    eventType = "No Event Type or Specific Event Type Specified";
                }
            }
        }
        public abstract void ExecuteLogData();

        public abstract void SetEventInformation(string key, string value, int index = 0);

        public abstract void SetValues(int index, object value);

        public abstract void SetValues(string key, object value);

        public virtual string GetEventKey(string valueOfKey)
        {
            foreach(var info in specificEventInformation)
            {
                if (info.Value == valueOfKey)
                {
                    return info.Value;
                }
            }
            return null;
        }

    }

    // Both entry kinds below build a NEW dictionary for every event
    // (mhs-updates/gamelogger-fix-093026). Each used to keep one dictionary,
    // clear and refill it on every call, and hand that same object to the
    // logger, which kept a reference to it in the queued entry. Any entry
    // still waiting to be sent then showed the details of the next event
    // from the same component. GameLogger.LogEvent copies the data as well,
    // so nothing here is shared with the queue.

    // Values as they go into an entry's data: vectors become {x, y, z} objects
    // (the shape LogTopoMapEvent and LogPlayerPositionEvent use) instead of a
    // serializer's dump of the struct's properties.
    internal static class LogValues
    {
        public static object ToLogValue(object value)
        {
            switch (value)
            {
                case Vector3 v3:
                    return new Dictionary<string, object> { { "x", v3.x }, { "y", v3.y }, { "z", v3.z } };
                case Vector2 v2:
                    return new Dictionary<string, object> { { "x", v2.x }, { "y", v2.y } };
                default:
                    return value;
            }
        }
    }

    [Serializable]
    public class ScriptableVariableEntry<TVar, TValue> :
        LoggingDataEntry where TVar : ScriptableVariable<TValue>
    {
        [Serializable]
        public struct EventLog
        {
            [Tooltip("The key for the log entry, for event specific details")]
            public string key;
            public TVar value;
        }

        [SerializeField] public List<EventLog> logEntries;

        public override void ExecuteLogData()
        {
            if (GameLogger.Instance == null) return;

            var logDictionary = new Dictionary<string, object>();

            string eventKey = string.Empty;

            for (int i = 0; i < specificEventInformation.Count; i++)
            {
                logDictionary.Add(specificEventInformation[i].Key, specificEventInformation[i].Value);
                if (specificEventInformation[i].Value == "DialogeNodeEvent")
                {
                    eventKey = $"{specificEventInformation[i].Value}:{ValueOf(logEntries[0].value)}:{ValueOf(logEntries[1].value)}";
                }

                if (eventType == "QuestEvent")
                {
                    eventKey = $"{specificEventInformation[i].Value}:{ValueOf(logEntries[0].value)}";
                }
            }

            // The variable's current value, not the variable object: the
            // object would be read when the entry is serialized (at send
            // time), the value is what it is now.
            foreach (EventLog entry in logEntries)
            {
                logDictionary.Add(entry.key, LogValues.ToLogValue(ValueOf(entry.value)));
            }

            if (eventKey == string.Empty)
            {
                GameLogger.Instance.LogEvent(eventType, logDictionary);
            }
            else
            {
                GameLogger.Instance.LogEvent(eventType, logDictionary, eventKey);
            }
        }

        protected static object ValueOf(TVar variable)
        {
            return variable != null ? (object)variable.Value : null;
        }

        public override void SetEventInformation(string key, string value, int index = 0)
        {
            var temp = specificEventInformation[index];
            temp.Key = key;
            temp.Value = value;
            specificEventInformation[index] = temp;
        }

        public override void SetValues(int index, object value)
        {
            var temp = logEntries[index];
            temp.value = (TVar)value;
            logEntries[index] = temp;
        }

        /// <summary>
        /// Sets the value of a specic logEntry based on the key
        /// </summary>
        /// <param name="key">The key to look up e.g. "ConversationId"</param>
        /// <param name="value">The value of the key e.g. 91</param>
        public override void SetValues(string key, object value)
        {
            for (int i = 0; i < logEntries.Count; i++)
            {
                if (logEntries[i].key == key)
                {
                    var temp = logEntries[i];
                    temp.value = (TVar)value;
                    logEntries[i] = temp;
                }
            }
        }
    }

    public class Vector2VariableEntry : ScriptableVariableEntry<Vector2Variable, Vector2> { }

    public class Vector3VariableEntry : ScriptableVariableEntry<Vector3Variable, Vector3>
    {
        public override void ExecuteLogData()
        {
            if (eventType == "TopographicMapEvent")
            {
                var logger = GameLogger.Instance;
                if (logger == null) return;
                Vector3 location = logEntries.Count > 0 && logEntries[0].value != null ? logEntries[0].value.Value : default;
                logger.LogTopoMapEvent(specificEventInformation[0].Value, specificEventInformation[1].Value, location);
            }
            else
            {
                base.ExecuteLogData();
            }
        }
    }

    public class StringVariableEntry : ScriptableVariableEntry<StringVariable, string> { }

    public class BoolVariableEntry : ScriptableVariableEntry<BoolVariable, bool> { }

    public class IntVariableEntry : ScriptableVariableEntry<IntVariable, int> { }


    [Serializable]
    public class LogEntry<T> : LoggingDataEntry
    {
        [Serializable]
        public struct EventLog
        {
            [Tooltip("The key for the log entry, for event specific details")]
            public string key;
            [Tooltip("The value for the log entry, for event specific details")]
            public T value;
        }

        [SerializeField] public List<EventLog> logEntries;

        public override void ExecuteLogData()
        {
            if (GameLogger.Instance == null) return;

            var logDictionary = new Dictionary<string, object>();
            string eventKey = string.Empty;

            for (int i = 0; i < specificEventInformation.Count; i++)
            {
                logDictionary.Add(specificEventInformation[i].Key, specificEventInformation[i].Value);
                if (specificEventInformation[i].Value == "DialogueNodeEvent")
                {
                    eventKey = $"{specificEventInformation[i].Value}:{logEntries[0].value}:{logEntries[1].value}";
                }

                if( eventType == "questEvent")
                {
                    eventKey = $"{specificEventInformation[i].Value}:{logEntries[0].value}";
                }

            }

            foreach (EventLog entry in logEntries)
            {
                logDictionary.Add(entry.key, LogValues.ToLogValue(entry.value));
            }

            if (eventKey == string.Empty)
            {
                GameLogger.Instance.LogEvent(eventType, logDictionary);
            }
            else
            {
                GameLogger.Instance.LogEvent(eventType, logDictionary, eventKey);
            }
        }

        public override void SetEventInformation(string key, string value, int index = 0)
        {
            var temp = specificEventInformation[index];
            temp.Key = key;
            temp.Value = value;
            specificEventInformation[index] = temp;
        }

        public override void SetValues(int index, object value)
        {
            // Create a temporary variable to hold the struct, modify it, and then assign it back to the list.
            var temp = logEntries[index];
            temp.value = (T)value;
            logEntries[index] = temp;
        }

        /// <summary>
        /// Sets the value of a specic logEntry based on the key
        /// </summary>
        /// <param name="key">The key to look up e.g. "ConversationId"</param>
        /// <param name="value">The value of the key e.g. 91</param>
        public override void SetValues(string key, object value)
        {
            for (int i = 0; i < logEntries.Count; i++)
            {
                if (logEntries[i].key == key)
                {
                    var temp = logEntries[i];
                    temp.value = (T)value;
                    logEntries[i] = temp;
                }
            }
        }

    }

    [Serializable]
    public class ObjectEntry : LogEntry<object> { }

    [Serializable]
    public class StringEntry : LogEntry<string> { }

    [Serializable]
    public class IntEntry : LogEntry<int> { }

    [Serializable]
    public class FloatEntry : LogEntry<float> { }

    [Serializable]
    public class BoolEntry : LogEntry<bool> { }

    [Serializable]
    public class Vector2Entry : LogEntry<Vector2> { }

    [Serializable]
    public class Vector3Entry : LogEntry<Vector3> { }

}
