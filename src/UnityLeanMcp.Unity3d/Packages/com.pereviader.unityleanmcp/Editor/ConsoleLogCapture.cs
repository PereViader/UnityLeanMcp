using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class ConsoleLogCapture : IDisposable
    {
        private const int MaxEntries = 2000;
        private const int MaxMessageCharacters = 4 * 1024;
        private const int MaxTotalMessageCharacters = 32 * 1024;
        private const string EntryTruncationMarker = "... (truncated: log entry exceeded 4,096 characters)";
        private const string OutputTruncationMarker = "[UnityLeanMcp: Console log output truncated (exceeded 32,768 characters)]";
        private readonly object m_Lock = new object();
        private readonly List<ConsoleLogEntry> m_Logs = new List<ConsoleLogEntry>();
        private volatile bool m_Disposed = false;
        private bool m_LimitReached = false;
        private int m_TotalMessageCharacters = 0;

        public ConsoleLogCapture()
        {
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        private void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            if (m_Disposed || condition == null)
            {
                return;
            }

            // Filter out internal UnityLeanMcp harness messages
            if (condition.StartsWith("UnityLeanMcp:", StringComparison.Ordinal))
            {
                return;
            }

            lock (m_Lock)
            {
                if (m_Disposed)
                {
                    return;
                }

                string boundedCondition = Truncate(condition);
                int maxStoredCharacters = MaxTotalMessageCharacters - OutputTruncationMarker.Length;
                if (m_Logs.Count < MaxEntries &&
                    m_TotalMessageCharacters + boundedCondition.Length <= maxStoredCharacters)
                {
                    m_Logs.Add(new ConsoleLogEntry
                    {
                        message = boundedCondition,
                        logType = type.ToString()
                    });
                    m_TotalMessageCharacters += boundedCondition.Length;
                }
                else if (!m_LimitReached)
                {
                    m_LimitReached = true;
                    int remainingCharacters = MaxTotalMessageCharacters - m_TotalMessageCharacters;
                    if (remainingCharacters > 0)
                    {
                        string marker = OutputTruncationMarker.Length <= remainingCharacters
                            ? OutputTruncationMarker
                            : OutputTruncationMarker.Substring(0, remainingCharacters);
                        m_Logs.Add(new ConsoleLogEntry
                        {
                            message = marker,
                            logType = LogType.Warning.ToString()
                        });
                        m_TotalMessageCharacters += marker.Length;
                    }
                }
            }
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxMessageCharacters)
            {
                return value ?? string.Empty;
            }

            int prefixLength = MaxMessageCharacters - EntryTruncationMarker.Length;
            if (prefixLength > 0 && char.IsHighSurrogate(value[prefixLength - 1]))
            {
                prefixLength--;
            }

            return value.Substring(0, prefixLength) + EntryTruncationMarker;
        }

        public List<ConsoleLogEntry> GetLogs()
        {
            lock (m_Lock)
            {
                return new List<ConsoleLogEntry>(m_Logs);
            }
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed)
                {
                    return;
                }
                m_Disposed = true;
            }

            Application.logMessageReceivedThreaded -= OnLogReceived;
        }
    }
}
