using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    internal static class UnityLeanMcpDispatcher
    {
        private static readonly ConcurrentQueue<Action> s_Queue = new ConcurrentQueue<Action>();
        private static bool s_Initialized;

        public static void EnsureInitialized()
        {
            if (s_Initialized) return;
            s_Initialized = true;
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        public static void Enqueue(Action action)
        {
            s_Queue.Enqueue(action);
        }

        public static void Update()
        {
            while (s_Queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
    }
}
